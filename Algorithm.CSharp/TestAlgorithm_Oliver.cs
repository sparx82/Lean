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

using Accord.Math.Environments;

//using QLNet;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Slippage;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Day Trading Algorithm for SMI Futures with Improved Risk Management
    /// OPTIMIZED VERSION: Focuses on reducing false signals and managing downside risk
    /// Uses strict entry filters, adaptive position sizing, and trade pause logic
    /// </summary>
    /// <meta name="tag" content="day trading" />
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="intraday" />
    /// <meta name="tag" content="momentum strategy" />
    public class TestAlgorithm_Oliver : QCAlgorithm
    {
        private const string RootSymbol = Futures.Indices.SMI;
        private Future _future;
        private Symbol _continuousContractSymbol;
        private Symbol _currentMappedSymbol;

        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators = new Dictionary<Symbol, IDataConsolidator>();

        // Intraday Indicators (optimized for 5-minute bars)
        private ExponentialMovingAverage _emaFast;
        private ExponentialMovingAverage _emaSlow;
        private MovingAverageConvergenceDivergence _macd;
        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;
        private BollingerBands _bollingerBands;
        private SimpleMovingAverage _volumeSMA; // Volume filter

        // Price tracking for momentum confirmation
        private decimal _lastClose;
        private int _barsInPosition;
        private decimal _lastBarVolume;

        // Intraday session times (Europe/Zurich timezone - EUREX trading hours)
        private readonly int _sessionStartHour = 9;
        private readonly int _sessionStartMinute = 0;
        private readonly int _sessionEndHour = 17;
        private readonly int _sessionEndMinute = 30;

        // Trading session filtering - best hours
        private readonly int _bestTradingHourStart = 9;
        private readonly int _bestTradingHourEnd = 16;

        // Strategy parameters - BALANCED: Fewer trades but better quality
        private int _rsiOversold = 30;  // Standard (was 20)
        private int _rsiOverbought = 70; // Standard (was 80)
        private int _emaFastPeriod = 12;  // Slower crossovers (was 8)
        private int _emaSlowPeriod = 40; // Much slower (was 20)
        private int _rsiPeriod = 9;
        private int _bollingerPeriod = 20;
        private decimal _bollingerDeviation = 2.0m;
        private decimal _maxPositionDuration = 240;  // Longer holds (was 120)
        private decimal _positionSize = 0.08m;       // Much smaller (was 0.15)
        private decimal _maxLossPercent = 0.8m;      // Tighter (was 1.2)
        private decimal _profitTargetPercent = 1.5m; // Much higher (was 0.8)
        private decimal _maxDailyLoss = 5.0m;        // Tighter (was 8.0)
        private decimal _minVolumeMultiplier = 0.6m; // Volume filter (was 0.5)
        private int _consecutiveLossesBeforePause = 4; // Aggressive pause (was 6)

        private bool _invested => Portfolio.Invested;
        private int _tradesWon;
        private int _tradesLost;
        private decimal _cumulativePnL;
        private decimal _largestWin;
        private decimal _largestLoss;
        private int _consecutiveLosses;
        private bool _tradingPausedToday;
        private decimal _dayStartEquity;

        public override void Initialize()
        {
            SetTimeZone("Europe/Zurich");
            SetAccountCurrency("CHF");

            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);

            // Load strategy parameters
            _rsiOversold = Convert.ToInt32(GetParameter("rsiOversold", 30));
            _rsiOverbought = Convert.ToInt32(GetParameter("rsiOverbought", 70));
            _emaFastPeriod = Convert.ToInt32(GetParameter("emaFastPeriod", 12));
            _emaSlowPeriod = Convert.ToInt32(GetParameter("emaSlowPeriod", 40));
            _rsiPeriod = Convert.ToInt32(GetParameter("rsiPeriod", 9));
            _bollingerPeriod = Convert.ToInt32(GetParameter("bollingerPeriod", 20));
            _bollingerDeviation = Convert.ToDecimal(GetParameter("bollingerDeviation", 2.0m));
            _maxPositionDuration = Convert.ToInt32(GetParameter("maxPositionDuration", 240));
            _positionSize = Convert.ToDecimal(GetParameter("positionSize", 0.08m));
            _maxLossPercent = Convert.ToDecimal(GetParameter("maxLossPercent", 0.8m));
            _profitTargetPercent = Convert.ToDecimal(GetParameter("profitTargetPercent", 1.5m));
            _maxDailyLoss = Convert.ToDecimal(GetParameter("maxDailyLoss", 5.0m));
            _minVolumeMultiplier = Convert.ToDecimal(GetParameter("minVolumeMultiplier", 0.6m));
            _consecutiveLossesBeforePause = Convert.ToInt32(GetParameter("consecutiveLossesBeforePause", 4));

            if (Config.Get("environment") == "live-interactive")
            {
                var ticker = "SMI";
                // Live trading configuration
            }
            else
            {
                SetStartDate(2023, 1, 1);
                SetEndDate(2025, 11, 30);
                SetCash(100000);

                _future = AddFuture(RootSymbol, Resolution.Tick, dataMappingMode: DataMappingMode.LastTradingDay, 
                    dataNormalizationMode: DataNormalizationMode.BackwardsRatio, contractDepthOffset: 0);
                _future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(90));

                SetSecurityInitializer(security =>
                {
                    if (security.Type == SecurityType.Future)
                    {
                        security.SetFeeModel(new InteractiveBrokersFeeModel());
                        security.SetSlippageModel(new FuturesTickSlippageModel(impactConstant: 0.1m));
                    }
                });
            }

            _continuousContractSymbol = _future.Symbol;
            _dayStartEquity = Portfolio.TotalPortfolioValue;

            // Initialize Indicators for intraday trading
            _emaFast = new ExponentialMovingAverage(_emaFastPeriod);
            _emaSlow = new ExponentialMovingAverage(_emaSlowPeriod);
            _macd = new MovingAverageConvergenceDivergence(12, 26, 9);
            _rsi = new RelativeStrengthIndex(_rsiPeriod);
            _atr = new AverageTrueRange(14);
            _bollingerBands = new BollingerBands(_bollingerPeriod, _bollingerDeviation);
            _volumeSMA = new SimpleMovingAverage(20); // Track volume

            // Shorter warmup for intraday trading
            SetWarmUp(TimeSpan.FromDays(2));
        }
        public override void OnData(Slice data)
        {
            // We do NOT place trade logic here because this fires on every single Tick.
            // We only handle contract rollovers here.

            // Handle Contract Rollover (Symbol Changed Event)
            if (data.SymbolChangedEvents.ContainsKey(_continuousContractSymbol))
            {
                var changedEvent = data.SymbolChangedEvents[_continuousContractSymbol];
                var oldSymbol = changedEvent.OldSymbol;
                var newSymbol = changedEvent.NewSymbol;

                Log($"Rollover: {oldSymbol} -> {newSymbol}");

                // If we have an open position in the old contract, move it to the new one
                if (Portfolio[oldSymbol].Invested)
                {
                    var quantity = Portfolio[oldSymbol].Quantity;
                    Liquidate(oldSymbol, tag: "Rollover Liquidate");
                    MarketOrder(newSymbol, quantity, tag: "Rollover Re-entry");
                }

                _currentMappedSymbol = newSymbol;
            }
        }

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                if (security.Symbol.SecurityType == SecurityType.Future && !security.Symbol.IsCanonical())
                {
                    // Create a 5-minute Consolidator from TICKS for intraday trading
                    var tickConsolidator = new TickConsolidator(TimeSpan.FromMinutes(5));

                    tickConsolidator.DataConsolidated += OnFiveMinuteBar;

                    SubscriptionManager.AddConsolidator(security.Symbol, tickConsolidator);

                    _consolidators[security.Symbol] = tickConsolidator;

                    Log($"5min Consolidator attached to {security.Symbol}");
                }
            }

            foreach (var security in changes.RemovedSecurities)
            {
                if (_consolidators.TryGetValue(security.Symbol, out var consolidator))
                {
                    SubscriptionManager.RemoveConsolidator(security.Symbol, consolidator);

                    if (consolidator is TickConsolidator tc)
                    {
                        tc.DataConsolidated -= OnFiveMinuteBar;
                    }

                    _consolidators.Remove(security.Symbol);
                }
            }
        }

        /// <summary>
        /// Core intraday logic - fires every 5 minutes
        /// IMPROVED: Stricter entry filters, tighter stops, smaller positions, trade pause logic
        /// </summary>
        private void OnFiveMinuteBar(object sender, TradeBar bar)
        {
            _emaFast.Update(bar.EndTime, bar.Close);
            _emaSlow.Update(bar.EndTime, bar.Close);
            _macd.Update(bar.EndTime, bar.Close);
            _rsi.Update(bar.EndTime, bar.Close);
            _atr.Update(bar);
            _bollingerBands.Update(bar.EndTime, bar.Close);
            _volumeSMA.Update(bar.EndTime, (decimal)bar.Volume);
            _lastBarVolume = (decimal)bar.Volume;

            if (IsWarmingUp || !_emaSlow.IsReady || !_atr.IsReady || !_bollingerBands.IsReady || !_volumeSMA.IsReady) 
                return;

            var currentContract = bar.Symbol;
            if (!Securities.ContainsKey(currentContract)) 
                return;

            if (!Securities[bar.Symbol].Exchange.ExchangeOpen)
                return;

            // Reset daily loss tracking and pause counters at market open
            if (bar.EndTime.Hour == _sessionStartHour && bar.EndTime.Minute == _sessionStartMinute)
            {
                _dayStartEquity = Portfolio.TotalPortfolioValue;
                _tradingPausedToday = false;
                _consecutiveLosses = 0;  // 🔴 BUG FIX: Reset consecutive losses daily!
                Log($"═ NEW TRADING DAY ═ Opening Equity: {_dayStartEquity:F2} CHF | Resetting consecutive losses");
            }

            // End-of-day position closure (EUREX closes at 17:30)
            if (bar.EndTime.Hour == _sessionEndHour && bar.EndTime.Minute >= _sessionEndMinute - 5)
            {
                if (_invested)
                {
                    Liquidate(currentContract, "End-of-Day Close");
                    _barsInPosition = 0;
                    Log($"EOD Liquidation at {bar.EndTime:HH:mm}");
                }
                return;
            }

            // Only trade during EUREX hours (9:00 - 17:30)
            if (bar.EndTime.Hour < _sessionStartHour || 
                (bar.EndTime.Hour == _sessionEndHour && bar.EndTime.Minute > _sessionEndMinute))
                return;

            // Check daily loss limit
            var dailyLoss = Portfolio.TotalPortfolioValue - _dayStartEquity;
            if (dailyLoss < -((_maxDailyLoss / 100) * _dayStartEquity))
            {
                if (!_tradingPausedToday)
                {
                    Log($"⚠️  DAILY LOSS LIMIT REACHED: {dailyLoss:F2} CHF - PAUSING TRADES");
                    _tradingPausedToday = true;
                }
                if (_invested)
                {
                    Liquidate(currentContract, "Daily Loss Limit Exceeded");
                }
                return;
            }

            // --- POSITION MANAGEMENT ---
            if (_invested)
            {
                var holding = Portfolio[currentContract];
                var holdingPnLPercent = holding.UnrealizedProfitPercent;
                _barsInPosition++;

                if (holding.IsLong)
                {
                    // IMPROVED: Realistic profit target (0.5% instead of 3%)
                    if (holdingPnLPercent >= _profitTargetPercent / 100)
                    {
                        Liquidate(currentContract, $"Profit Target Hit (Long +{(holdingPnLPercent * 100):F2}%)");
                        _tradesWon++;
                        _consecutiveLosses = 0;
                        _barsInPosition = 0;
                        return;
                    }
                    
                    // IMPROVED: Tighter stop loss (0.8% instead of 2%)
                    if (holdingPnLPercent <= -(_maxLossPercent / 100))
                    {
                        Liquidate(currentContract, $"Stop Loss (Long {(holdingPnLPercent * 100):F2}%)");
                        _tradesLost++;
                        _consecutiveLosses++;
                        _barsInPosition = 0;
                        return;
                    }

                    // Bollinger Band exit: close if touches lower band
                    if (bar.Close < _bollingerBands.LowerBand.Current.Value)
                    {
                        Liquidate(currentContract, "Exit Long (Lower Bollinger)");
                        if (holdingPnLPercent > 0) _tradesWon++;
                        else { _tradesLost++; _consecutiveLosses++; }
                        _barsInPosition = 0;
                        return;
                    }

                    // Exit if price breaks below fast EMA significantly
                    if (bar.Close < _emaFast.Current.Value - (_atr * 0.5m))
                    {
                        Liquidate(currentContract, "Exit Long (EMA Break)");
                        if (holdingPnLPercent > 0) _tradesWon++;
                        else { _tradesLost++; _consecutiveLosses++; }
                        _barsInPosition = 0;
                        return;
                    }
                }
                else if (holding.IsShort)
                {
                    // IMPROVED: Realistic profit target
                    if (holdingPnLPercent >= _profitTargetPercent / 100)
                    {
                        Liquidate(currentContract, $"Profit Target Hit (Short +{(holdingPnLPercent * 100):F2}%)");
                        _tradesWon++;
                        _consecutiveLosses = 0;
                        _barsInPosition = 0;
                        return;
                    }

                    // IMPROVED: Tighter stop loss
                    if (holdingPnLPercent <= -(_maxLossPercent / 100))
                    {
                        Liquidate(currentContract, $"Stop Loss (Short {(holdingPnLPercent * 100):F2}%)");
                        _tradesLost++;
                        _consecutiveLosses++;
                        _barsInPosition = 0;
                        return;
                    }

                    // Bollinger Band exit: close if touches upper band
                    if (bar.Close > _bollingerBands.UpperBand.Current.Value)
                    {
                        Liquidate(currentContract, "Exit Short (Upper Bollinger)");
                        if (holdingPnLPercent > 0) _tradesWon++;
                        else { _tradesLost++; _consecutiveLosses++; }
                        _barsInPosition = 0;
                        return;
                    }

                    // Exit if price breaks above fast EMA significantly
                    if (bar.Close > _emaFast.Current.Value + (_atr * 0.5m))
                    {
                        Liquidate(currentContract, "Exit Short (EMA Break)");
                        if (holdingPnLPercent > 0) _tradesWon++;
                        else { _tradesLost++; _consecutiveLosses++; }
                        _barsInPosition = 0;
                        return;
                    }
                }

                // IMPROVED: Shorter max duration (60 bars = ~5 hours)
                if (_barsInPosition > _maxPositionDuration)
                {
                    Liquidate(currentContract, $"Max Duration Reached ({_barsInPosition} bars)");
                    if (holding.UnrealizedProfitPercent > 0) _tradesWon++;
                    else { _tradesLost++; _consecutiveLosses++; }
                    _barsInPosition = 0;
                    return;
                }
            }

            // --- ENTRY SIGNALS (BALANCED: Quality over Quantity) ---
            if (!_invested && !_tradingPausedToday && _consecutiveLosses < _consecutiveLossesBeforePause)
            {
                // Volume filter - skip low volume bars
                var avgVolume = _volumeSMA.Current.Value;
                if (_lastBarVolume < avgVolume * _minVolumeMultiplier)
                    return;

                var macdHistogram = _macd.Histogram.Current.Value;
                var macdValue = _macd.Current.Value; // MACD value
                
                // BALANCED: Simple but effective entry conditions
                // Long Setup: EMA cross + MACD confirmation
                if (_emaFast.Current.Value > _emaSlow.Current.Value && 
                    macdHistogram > 0 && 
                    macdValue > 0 &&  // MACD above signal line
                    _rsi.Current.Value < _rsiOverbought && 
                    _rsi.Current.Value > 35 &&  // RSI in bullish zone
                    _lastBarVolume > avgVolume * 0.6m)  // Volume confirmation
                {
                    SetHoldings(currentContract, _positionSize);
                    _barsInPosition = 1;
                    Log($"✓ LONG Entry at {bar.Close:F2}, EMA:{_emaFast.Current.Value:F2}/{_emaSlow.Current.Value:F2}, RSI:{_rsi.Current.Value:F2}, MACD:{macdHistogram:F6}");
                }
                
                // Short Setup: EMA cross + MACD confirmation
                else if (_emaFast.Current.Value < _emaSlow.Current.Value && 
                         macdHistogram < 0 && 
                         macdValue < 0 &&  // MACD below signal line
                         _rsi.Current.Value > _rsiOversold && 
                         _rsi.Current.Value < 65 &&  // RSI in bearish zone
                         _lastBarVolume > avgVolume * 0.6m)  // Volume confirmation
                {
                    SetHoldings(currentContract, -_positionSize);
                    _barsInPosition = 1;
                    Log($"✓ SHORT Entry at {bar.Close:F2}, EMA:{_emaFast.Current.Value:F2}/{_emaSlow.Current.Value:F2}, RSI:{_rsi.Current.Value:F2}, MACD:{macdHistogram:F6}");
                }
            }

            _lastClose = bar.Close;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                var security = Securities[orderEvent.Symbol];
                var culture = System.Globalization.CultureInfo.InvariantCulture;

                if (security.Holdings.Quantity == 0)
                {
                    var tradePnL = security.Holdings.LastTradeProfit;
                    var totalEquity = Portfolio.TotalPortfolioValue;
                    var winRatio = _tradesWon + _tradesLost > 0 ? 
                        (decimal)_tradesWon / (_tradesWon + _tradesLost) : 0;
                    
                    // Update cumulative statistics
                    _cumulativePnL += tradePnL;
                    
                    // Track largest win/loss
                    if (tradePnL > _largestWin)
                        _largestWin = tradePnL;
                    if (tradePnL < _largestLoss)
                        _largestLoss = tradePnL;

                    var totalTrades = _tradesWon + _tradesLost;
                    var status = tradePnL > 0 ? "✓ WIN" : "✗ LOSS";
                    var dailyLoss = totalEquity - _dayStartEquity;

                    Log($"┌─────────────────────────────────────────────────────────────┐");
                    Log($"│ TRADE #{totalTrades} {status} | {orderEvent.UtcTime:yyyy-MM-dd HH:mm:ss}");
                    Log($"├─────────────────────────────────────────────────────────────┤");
                    Log($"│ Trade P&L:          {tradePnL.ToString("F2", culture),10} CHF");
                    Log($"│ Cumulative P&L:     {_cumulativePnL.ToString("F2", culture),10} CHF");
                    Log($"│ Today's P&L:        {dailyLoss.ToString("F2", culture),10} CHF");
                    Log($"│ Account Equity:     {totalEquity.ToString("F2", culture),10} CHF");
                    Log($"├─────────────────────────────────────────────────────────────┤");
                    Log($"│ W/L:                {_tradesWon}/{_tradesLost} | Win Ratio: {(winRatio * 100):F1}%");
                    Log($"│ Consecutive Losses: {_consecutiveLosses}");
                    Log($"├─────────────────────────────────────────────────────────────┤");
                    Log($"│ Largest Win:        {_largestWin.ToString("F2", culture),10} CHF");
                    Log($"│ Largest Loss:       {_largestLoss.ToString("F2", culture),10} CHF");
                    Log($"│ Return on Capital:  {(_cumulativePnL / 100000 * 100):F2}%");
                    Log($"└─────────────────────────────────────────────────────────────┘");
                    
                    Debug($"TRADE_EVENT,CLOSED,{orderEvent.Symbol},{orderEvent.UtcTime:yyyy-MM-dd HH:mm:ss}," +
                        $"{tradePnL.ToString(culture)},{_cumulativePnL.ToString(culture)}," +
                        $"{totalEquity.ToString(culture)},W:{_tradesWon},L:{_tradesLost},WinRatio:{(winRatio * 100):F1}%");
                }
                else
                {
                    Debug($"TRADE_EVENT,ENTRY,{orderEvent.Symbol},{orderEvent.UtcTime:yyyy-MM-dd HH:mm:ss}," +
                        $"{orderEvent.FillPrice.ToString(culture)},{orderEvent.FillQuantity.ToString(culture)}");
                }
            }
        }
    }

    /// <summary>
    /// A Slippage Model optimized for Index Futures with Tick Data.
    /// It simulates crossing the spread and market impact based on order size.
    /// </summary>
    public class FuturesTickSlippageModel : ISlippageModel
    {
        private readonly decimal _impactConstant;

        /// <summary>
        /// Initializes the model.
        /// </summary>
        /// <param name="impactConstant">Coefficient for the Square-Root Law (default 0.1).</param>
        public FuturesTickSlippageModel(decimal impactConstant = 0.1m)
        {
            _impactConstant = impactConstant;
        }

        public decimal GetSlippageApproximation(Security asset, Order order)
        {
            // 1. Basic Guard Clauses
            var lastData = asset.GetLastData();
            if (lastData == null) return 0m;

            // We only apply this logic to Market Orders. 
            // Limit orders generally require a FillModel to determine execution, not just slippage.
            if (order.Type != OrderType.Market)
            {
                return 0m;
            }

            // 2. Component A: Crossing the Spread
            // If we Buy, we want to fill at Ask. If we Sell, fill at Bid.
            // Standard Fill Models often fill at "Last Trade Price".
            // Slippage = |TargetQuote - LastTradePrice|

            decimal spreadSlippage = 0m;
            decimal currentPrice = asset.Price; // Usually Last Trade

            // Ensure we have valid Quote data (Tick data usually includes Bid/Ask)
            if (asset.AskPrice > 0 && asset.BidPrice > 0)
            {
                if (order.Direction == OrderDirection.Buy)
                {
                    // If Last Trade is 100, but Ask is 100.25, we need 0.25 slippage
                    spreadSlippage = Math.Max(0, asset.AskPrice - currentPrice);
                }
                else if (order.Direction == OrderDirection.Sell)
                {
                    // If Last Trade is 100, but Bid is 99.75, we need 0.25 slippage
                    spreadSlippage = Math.Max(0, currentPrice - asset.BidPrice);
                }
            }
            else
            {
                // Fallback: If no quotes available, assume 1 tick slippage
                spreadSlippage = asset.SymbolProperties.MinimumPriceVariation;
            }

            // 3. Component B: Market Impact (Square-Root Law)
            // Impact = Constant * Volatility * sqrt(OrderSize / TotalVolume)
            // Since we are doing per-tick, we simplify to: Constant * sqrt(Order / LastTickVolume)

            decimal impactSlippage = 0m;
            decimal lastTickVolume = 0m;

            if (lastData is Tick tick)
            {
                lastTickVolume = tick.Quantity;
            }
            // If using TradeBars instead of raw ticks, use Volume
            else if (lastData is TradeBar bar)
            {
                lastTickVolume = bar.Volume;
            }

            // Avoid division by zero
            if (lastTickVolume > 0)
            {
                // Simple implementation of Square-Root Impact
                var participationRatio = (double)(order.AbsoluteQuantity / lastTickVolume);
                impactSlippage = _impactConstant * (decimal)Math.Sqrt(participationRatio) * asset.SymbolProperties.MinimumPriceVariation;
            }

            // Total Slippage
            return spreadSlippage + impactSlippage;
        }
    }
}
