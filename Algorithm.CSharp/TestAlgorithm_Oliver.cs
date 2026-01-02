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
using QuantConnect.Securities;
using QuantConnect.Securities.Future;
using System;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using static QLNet.NumericHaganPricer;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// EMA cross with SP500 E-mini futures
    /// In this example, we demostrate how to trade futures contracts using
    /// a equity to generate the trading signals
    /// It also shows how you can prefilter contracts easily based on expirations.
    /// It also shows how you can inspect the futures chain to pick a specific contract to trade.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="indicators" />
    /// <meta name="tag" content="strategy example" />
    public class TestAlgorithm_Oliver : QCAlgorithm
    {
        private Future future;
        private Symbol _continuousSymbol;

        // Indicators
        private ExponentialMovingAverage _fastEma;
        private ExponentialMovingAverage _slowEma;
        private RelativeStrengthIndex _rsi;

        // Risk Management
        private decimal _trailingStopPercent = 0.03m;
        private decimal _highWaterMark = 0m;
        private decimal _lowWaterMark = 0m;

        public override void Initialize()
        {
            SetTimeZone("Europe/Zurich");
            SetAccountCurrency("CHF");

            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);

            //_emaWindow = Convert.ToInt32(GetParameter("emaWindow", 40));
            //_stopLossPoints = Convert.ToDecimal(GetParameter("stopLossPoints", 20m));
            //_takeProfitPoints = Convert.ToDecimal(GetParameter("takeProfitPoints", 100m));
            //_stoplossInital = Convert.ToInt32(GetParameter("stoplossInital", 50));

            if (Config.Get("environment") == "live-interactive")
            {
                var ticker = "SMI";
                //var targetExpiry = GetNextQuarterlyExpiry(Time);

                //Log($"[Init] Calculated Target Expiry: {targetExpiry.ToShortDateString()}");

                //_continuousSymbol = QuantConnect.Symbol.CreateFuture(ticker, Market.EUREX, targetExpiry);
                AddFutureContract(_continuousSymbol, Resolution.Tick);
            }
            else
            {
                SetStartDate(2020, 1, 1);
                SetEndDate(2025, 10, 31);

                future = AddFuture(Futures.Indices.SMI, Resolution.Minute, dataMappingMode: DataMappingMode.OpenInterest, dataNormalizationMode: DataNormalizationMode.BackwardsRatio);
                future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(182));

                // Set a security initializer to apply a Fee Model to everything
                SetSecurityInitializer(security =>
                {
                    if (security.Type == SecurityType.Future)
                    {
                        security.SetFeeModel(new InteractiveBrokersFeeModel());
                        //security.SetFeeModel(new SaxoFeeModel());
                    }
                });
            }

            _continuousSymbol = future.Symbol;

            // 3. Manual Indicator Setup (Consolidated Daily Bars)
            _fastEma = new ExponentialMovingAverage(50);
            _slowEma = new ExponentialMovingAverage(200);
            _rsi = new RelativeStrengthIndex(14, MovingAverageType.Wilders);

            // Aggregate Ticks into Daily Bars for stable indicators
            var dailyConsolidator = new MinuteConsolidator(TimeSpan.FromDays(1));

            dailyConsolidator.DataConsolidated += (sender, bar) =>
            {
                _fastEma.Update(bar.Time, bar.Price);
                _slowEma.Update(bar.Time, bar.Price);
                _rsi.Update(bar.Time, bar.Price);
            };

            SubscriptionManager.AddConsolidator(_continuousSymbol, dailyConsolidator);
            SetWarmUp(TimeSpan.FromDays(200));


        }

        /// <summary>
        /// Wird aufgerufen, wenn neue Tick-Daten eintreffen
        /// </summary>
        public override void OnData(Slice data)
        {
            // A. VALIDATION CHECKS
            if (!_fastEma.IsReady || !_slowEma.IsReady || !_rsi.IsReady) return;

            // Check if we have the mapped contract in the current data slice
            var currentContractSymbol = future.Mapped;
            if (!data.ContainsKey(currentContractSymbol)) return;

            // Get Tick Data
            var ticks = data.Ticks[currentContractSymbol];
            if (ticks == null || ticks.Count == 0) return;
            var currentPrice = ticks.Last().LastPrice;

            // B. ROLLOVER LOGIC
            // Check if we are holding any OLD contracts that are not the current "Mapped" one
            foreach (var holding in Portfolio.Values)
            {
                if (holding.Invested && holding.Symbol.SecurityType == SecurityType.Future)
                {
                    // If the holding is NOT the current front-month contract, we must Roll Over
                    if (holding.Symbol != currentContractSymbol)
                    {
                        Debug($"Rolling Over: Selling {holding.Symbol} -> Buying {currentContractSymbol}");

                        // 1. Liquidate the old position
                        Liquidate(holding.Symbol);

                        // 2. Open position in the new contract (Same direction)
                        // We re-enter based on the direction we were holding
                        if (holding.IsLong)
                        {
                            SetHoldings(currentContractSymbol, 1.0);
                            _highWaterMark = currentPrice; // Reset Stop for new price level
                        }
                        else if (holding.IsShort)
                        {
                            SetHoldings(currentContractSymbol, -1.0);
                            _lowWaterMark = currentPrice; // Reset Stop for new price level
                        }
                        return; // Exit OnData to let the trade settle
                    }
                }
            }

            // C. RISK MANAGEMENT (Trailing Stop on Current Contract)
            if (Portfolio[currentContractSymbol].Invested)
            {
                if (Portfolio[currentContractSymbol].IsLong)
                {
                    if (currentPrice > _highWaterMark) _highWaterMark = currentPrice;

                    if (currentPrice < _highWaterMark * (1 - _trailingStopPercent))
                    {
                        Liquidate(currentContractSymbol, "Trailing Stop Long");
                        return;
                    }
                }
                else if (Portfolio[currentContractSymbol].IsShort)
                {
                    if (currentPrice < _lowWaterMark) _lowWaterMark = currentPrice;

                    if (currentPrice > _lowWaterMark * (1 + _trailingStopPercent))
                    {
                        Liquidate(currentContractSymbol, "Trailing Stop Short");
                        return;
                    }
                }
            }

            // D. ENTRY LOGIC (Only if not invested)
            if (!Portfolio.Invested)
            {
                // Long: Trend Up + RSI not Overbought
                if (_fastEma > _slowEma && _rsi < 70)
                {
                    SetHoldings(currentContractSymbol, 1.0);
                    _highWaterMark = currentPrice;
                    Debug($"Long Entry at {currentPrice}");
                }
                // Short: Trend Down + RSI not Oversold
                else if (_fastEma < _slowEma && _rsi > 30)
                {
                    SetHoldings(currentContractSymbol, -1.0);
                    _lowWaterMark = currentPrice;
                    Debug($"Short Entry at {currentPrice}");
                }
            }
        }
    }
}
