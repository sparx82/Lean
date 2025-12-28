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
    public class FutureVWAPAlgorithm_Oliver : QCAlgorithm
    {
        private Future _future;
        private VolumeWeightedAveragePriceIndicator _vwap;
        private Symbol _currentContract;

        // Parameter: Rolling period for VWAP (50 * 1-minute bars)
        private const int VwapPeriod = 50;

        public override void Initialize()
        {
            SetTimeZone("Europe/Zurich");
            SetAccountCurrency("CHF");

            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);

            SetStartDate(2024, 1, 1);
            SetEndDate(2024, 12, 31);

            _future = AddFuture(Futures.Indices.SMI, Resolution.Tick, dataMappingMode: DataMappingMode.LastTradingDay, dataNormalizationMode: DataNormalizationMode.BackwardsRatio);
            _future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(90));

            // Set a security initializer to apply a Fee Model to everything
            SetSecurityInitializer(security =>
            {
                if (security.Type == SecurityType.Future)
                {
                    security.SetFeeModel(new InteractiveBrokersFeeModel());
                }
            });

            _vwap = new VolumeWeightedAveragePriceIndicator(VwapPeriod);
            var tickConsolidator = new TickConsolidator(TimeSpan.FromMinutes(1));
            tickConsolidator.DataConsolidated += OnMinuteBar;
            SubscriptionManager.AddConsolidator(_future.Symbol, tickConsolidator);

            // 4. Warm up
            // Requesting Minute history will automatically aggregate historical ticks for us
            var history = History(_future.Symbol, VwapPeriod, Resolution.Minute);
            foreach (var bar in history)
            {
                _vwap.Update(bar);
            }
        }

        /// <summary>
        /// This event fires once every minute, when the consolidator finishes a bar.
        /// </summary>
        private void OnMinuteBar(object sender, TradeBar bar)
        {
            // Update VWAP with the consolidated bar (Open, High, Low, Close, Volume)
            _vwap.Update(bar);

            if (!_vwap.IsReady) return;

            // Update mapped contract (the one we actually trade)
            _currentContract = _future.Mapped;

            var currentPrice = bar.Close;
            var vwapValue = _vwap.Current.Value;

            // Execution Logic (Same as before, but runs on confirmed bars)
            if (!Portfolio.Invested)
            {
                if (currentPrice > vwapValue)
                {
                    SetHoldings(_currentContract, 1);
                    Debug($"Entry Long: Price {currentPrice} > VWAP {vwapValue}, Date {bar.EndTime}");
                }
                else if (currentPrice < vwapValue)
                {
                    SetHoldings(_currentContract, -1);
                    Debug($"Entry Short: Price {currentPrice} < VWAP {vwapValue}, Date {bar.EndTime}");
                }
            }
            else
            {
                // Exit/Reversal Logic
                if (Portfolio[_currentContract].IsLong && currentPrice < vwapValue)
                {
                    Liquidate(_currentContract);
                    Debug($"Exit Long: Price {currentPrice} crossed below VWAP {vwapValue}, Date {bar.EndTime}");
                }
                else if (Portfolio[_currentContract].IsShort && currentPrice > vwapValue)
                {
                    Liquidate(_currentContract);
                    Debug($"Exit Short: Price {currentPrice} crossed above VWAP {vwapValue}, Date {bar.EndTime}");
                }
            }
        }
        public override void OnData(Slice slice)
        { }
        
        public override void OnSymbolChangedEvents(SymbolChangedEvents symbolChangedEvents)
        {
            foreach (var symbol in symbolChangedEvents.Keys)
            {
                if (symbol == _future.Symbol)
                {
                    var changedEvent = symbolChangedEvents[symbol];
                    var oldSymbol = changedEvent.OldSymbol;
                    Log($"Rollover triggered: {oldSymbol} -> {changedEvent.NewSymbol}");

                    if (Portfolio[oldSymbol].Invested)
                    {
                        Liquidate(oldSymbol, tag: "Futures Rollover - Liquidating Old");
                    }
                }
            }
        }
    }
}
