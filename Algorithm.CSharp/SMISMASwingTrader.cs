/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// SMI Futures SMA Crossover Strategy
    /// 
    /// Simple Mean Reversal Strategy:
    /// - Trades on 5-minute bars
    /// - SMA 10 (10 periods of 5-min bars = ~50 minutes)
    /// - SMA 50 (50 periods of 5-min bars = ~250 minutes)
    /// - BUY when SMA10 > SMA50 (uptrend)
    /// - SELL/SHORT when SMA10 < SMA50 (downtrend)
    /// - Takes profits at 1.5% gains
    /// - Stops losses at 1%
    /// 
    /// Timeframes:
    /// - SMA10: ~50 minutes of data
    /// - SMA50: ~250 minutes of data (~4 hours)
    /// </summary>
    /// <meta name="tag" content="swing trading" />
    /// <meta name="tag" content="sma crossover" />
    /// <meta name="tag" content="futures" />
    public class SMISMASwingTrader : QCAlgorithm
    {
        private const string RootSymbol = Futures.Indices.SMI;
        private Future _future;
        private Symbol _continuousContractSymbol;

        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators = new Dictionary<Symbol, IDataConsolidator>();

        // 5-minute indicators
        private SimpleMovingAverage _sma10;
        private SimpleMovingAverage _sma50;

        // State tracking
        private int _barsProcessed = 0;
        private int _tradesWon = 0;
        private int _tradesLost = 0;
        private decimal _cumulativePnL = 0m;
        private decimal _entryPrice = 0m;
        private int _barsInTrade = 0;

        // Strategy parameters
        private decimal _profitTargetPercent = 1.5m;
        private decimal _stopLossPercent = 1.0m;
        private decimal _positionSize = 0.2m;
        private int _maxHoldBars = 288;  // ~1 day of 5-minute bars

        // Previous signal state to detect crossovers
        private bool _previousSMA10Greater = false;
        private bool _sma10Greater = false;

        private bool _invested => Portfolio.Invested;

        public override void Initialize()
        {
            SetTimeZone("Europe/Zurich");
            SetAccountCurrency("CHF");
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);

            SetStartDate(2023, 1, 1);
            SetEndDate(2023, 6, 30);
            SetCash(100000);

            // Add SMI futures with TICK resolution
            _future = AddFuture(RootSymbol, Resolution.Tick,
                dataMappingMode: DataMappingMode.LastTradingDay,
                dataNormalizationMode: DataNormalizationMode.BackwardsRatio,
                contractDepthOffset: 0);
            _future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(90));

            SetSecurityInitializer(security =>
            {
                if (security.Type == SecurityType.Future)
                {
                    security.SetFeeModel(new InteractiveBrokersFeeModel());
                }
            });

            _continuousContractSymbol = _future.Symbol;

            // Initialize SMAs
            _sma10 = new SimpleMovingAverage(10);
            _sma50 = new SimpleMovingAverage(50);

            SetWarmUp(TimeSpan.FromDays(2));
        }

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                if (security.Symbol.SecurityType == SecurityType.Future && !security.Symbol.IsCanonical())
                {
                    // Create 5-minute consolidator from tick data
                    var fiveMinConsolidator = new TickConsolidator(TimeSpan.FromMinutes(5));
                    fiveMinConsolidator.DataConsolidated += OnFiveMinBar;
                    SubscriptionManager.AddConsolidator(security.Symbol, fiveMinConsolidator);
                    _consolidators[security.Symbol] = fiveMinConsolidator;

                    Log($"✓ 5-minute consolidator attached to {security.Symbol}");
                }
            }

            foreach (var security in changes.RemovedSecurities)
            {
                if (_consolidators.TryGetValue(security.Symbol, out var consolidator))
                {
                    SubscriptionManager.RemoveConsolidator(security.Symbol, consolidator);
                    _consolidators.Remove(security.Symbol);
                }
            }
        }

        private void OnFiveMinBar(object sender, TradeBar bar)
        {
            if (IsWarmingUp) return;

            ProcessBar(bar);
        }

        private void ProcessBar(TradeBar bar)
        {
            var currentContract = bar.Symbol;

            if (!Securities.ContainsKey(currentContract))
                return;

            // Update indicators
            _sma10.Update(bar.EndTime, bar.Close);
            _sma50.Update(bar.EndTime, bar.Close);

            if (!_sma10.IsReady || !_sma50.IsReady)
                return;

            _barsProcessed++;

            // Log every 20 bars
            if (_barsProcessed % 20 == 0)
            {
                Log($"[BAR] #{_barsProcessed} {bar.EndTime:yyyy-MM-dd HH:mm} Close:{bar.Close:F2} SMA10:{_sma10.Current.Value:F2} SMA50:{_sma50.Current.Value:F2} Invested:{_invested}");
            }

            // Store previous state
            _previousSMA10Greater = _sma10Greater;
            _sma10Greater = _sma10.Current.Value > _sma50.Current.Value;

            // --- POSITION MANAGEMENT ---
            if (_invested)
            {
                var holding = Portfolio[currentContract];
                var pnlPercent = holding.UnrealizedProfitPercent;
                _barsInTrade++;

                // Take profits at target
                if (pnlPercent >= _profitTargetPercent / 100)
                {
                    Log($"[EXIT] Profit Target: P&L={pnlPercent * 100:F2}%");
                    Liquidate(currentContract, tag: "Profit Target");
                    _tradesWon++;
                    LogTrade(pnlPercent, "Profit Target");
                    return;
                }

                // Stop loss
                if (pnlPercent <= -(_stopLossPercent / 100))
                {
                    Log($"[EXIT] Stop Loss: P&L={pnlPercent * 100:F2}%");
                    Liquidate(currentContract, tag: "Stop Loss");
                    _tradesLost++;
                    LogTrade(pnlPercent, "Stop Loss");
                    return;
                }

                // Exit after max hold period
                if (_barsInTrade >= _maxHoldBars)
                {
                    Log($"[EXIT] Time Exit: {_barsInTrade} bars");
                    Liquidate(currentContract, tag: "Time Exit");
                    if (pnlPercent > 0) _tradesWon++;
                    else _tradesLost++;
                    LogTrade(pnlPercent, "Time Exit");
                    return;
                }
            }

            // --- ENTRY SIGNALS (SMA Crossover) ---
            if (!_invested)
            {
                // Bullish crossover: SMA10 just crossed above SMA50
                if (!_previousSMA10Greater && _sma10Greater)
                {
                    var contractQty = (int)(Portfolio.Cash * _positionSize / (bar.Close * _future.SymbolProperties.ContractMultiplier));
                    if (contractQty > 0)
                    {
                        Log($"[ENTRY] BUY Signal #{_barsProcessed}: SMA10={_sma10.Current.Value:F2} > SMA50={_sma50.Current.Value:F2}");
                        MarketOrder(currentContract, contractQty, tag: "SMA Bullish");
                        _entryPrice = bar.Close;
                        _barsInTrade = 0;
                    }
                    return;
                }

                // Bearish crossover: SMA10 just crossed below SMA50
                if (_previousSMA10Greater && !_sma10Greater)
                {
                    var contractQty = (int)(Portfolio.Cash * _positionSize / (bar.Close * _future.SymbolProperties.ContractMultiplier));
                    if (contractQty > 0)
                    {
                        Log($"[ENTRY] SHORT Signal #{_barsProcessed}: SMA10={_sma10.Current.Value:F2} < SMA50={_sma50.Current.Value:F2}");
                        MarketOrder(currentContract, -contractQty, tag: "SMA Bearish");
                        _entryPrice = bar.Close;
                        _barsInTrade = 0;
                    }
                    return;
                }
            }
        }

        private void LogTrade(decimal pnlPercent, string exitReason)
        {
            var pnl = Portfolio[_continuousContractSymbol].LastTradeProfit;
            _cumulativePnL += pnl;
            var totalTrades = _tradesWon + _tradesLost;
            var winRate = _tradesWon + _tradesLost > 0 ? (decimal)_tradesWon / (_tradesWon + _tradesLost) : 0;

            Log($"┌────────────────────────────────────────┐");
            Log($"│ TRADE #{totalTrades} {exitReason}");
            Log($"├────────────────────────────────────────┤");
            Log($"│ Entry: {_entryPrice:F2}");
            Log($"│ P&L: {pnl:F2} CHF ({pnlPercent * 100:F2}%)");
            Log($"│ Cumulative: {_cumulativePnL:F2} CHF");
            Log($"│ W/L: {_tradesWon}/{_tradesLost} (Win Rate: {winRate * 100:F1}%)");
            Log($"│ Equity: {Portfolio.TotalPortfolioValue:F2} CHF");
            Log($"└────────────────────────────────────────┘");
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                if (Portfolio[orderEvent.Symbol].Quantity != 0 && _entryPrice == 0m)
                {
                    _entryPrice = orderEvent.FillPrice;
                    _barsInTrade = 1;
                    Log($"Position opened: {orderEvent.Symbol} @ {orderEvent.FillPrice:F2}");
                }
                else if (Portfolio[orderEvent.Symbol].Quantity == 0)
                {
                    _entryPrice = 0m;
                }
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"\n╔════════════════════════════════════════╗");
            Log($"║ ALGORITHM COMPLETE - SUMMARY           ║");
            Log($"╠════════════════════════════════════════╣");
            Log($"║ Total Bars Processed: {_barsProcessed}");
            Log($"║ Total Trades: {_tradesWon + _tradesLost}");
            Log($"║ Wins: {_tradesWon}  |  Losses: {_tradesLost}");
            Log($"║ Win Rate: {(_tradesWon + _tradesLost > 0 ? (decimal)_tradesWon / (_tradesWon + _tradesLost) * 100 : 0):F1}%");
            Log($"║ Cumulative P&L: {_cumulativePnL:F2} CHF");
            Log($"║ Final Equity: {Portfolio.TotalPortfolioValue:F2} CHF");
            Log($"╚════════════════════════════════════════╝");
        }
    }
}
