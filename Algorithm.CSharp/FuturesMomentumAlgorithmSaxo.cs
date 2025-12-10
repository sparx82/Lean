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
    public class FuturesMomentumAlgorithmSaxo : QCAlgorithm
    {
        private Symbol _futureSymbol;
        // Define your rolling window size (e.g., last 5 minutes of data)
        private int LookbackMinutes = 3;
        private RollingWindow<TradeBar> _priceWindow;

        private readonly decimal MomentumThreshold = 10.0m;

        private Symbol _activeSymbol;
        private Future future;

        private Symbol _activeContractSymbol = null;
        private FuturesContract _tradingContract;

        private decimal _entryPrice;
        private bool _invested;
        private OrderTicket _stopLossTicket;

        private int _activiationPriceDelta = 5;
        private int _entryDelta = 10;
        private int _trailingStopDelta = 2;

        private TickConsolidator _minuteConsolidator;

        public override void Initialize()
        {
            SetTimeZone("Europe/Zurich");
            SetAccountCurrency("CHF");

            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);

            _trailingStopDelta = Convert.ToInt32(GetParameter("trailingStopDelta", 2));
            _activiationPriceDelta = Convert.ToInt32(GetParameter("activationPriceDelta", 5));
            _entryDelta = Convert.ToInt32(GetParameter("entryDelta", 10));

            if (Config.Get("environment") == "live-interactive")
            {
                var ticker = "SMI";
                var targetExpiry = GetNextQuarterlyExpiry(Time);

                Log($"[Init] Calculated Target Expiry: {targetExpiry.ToShortDateString()}");

                _activeContractSymbol = QuantConnect.Symbol.CreateFuture(ticker, Market.EUREX, targetExpiry);
                AddFutureContract(_activeContractSymbol, Resolution.Tick);
            }
            else
            {
                SetStartDate(2024, 01, 1);
                SetEndDate(2024, 1, 10);

                future = AddFuture(Futures.Indices.SMI, Resolution.Tick, dataMappingMode: DataMappingMode.LastTradingDay, dataNormalizationMode: DataNormalizationMode.BackwardsRatio);
                future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(90));

                // Set a security initializer to apply a Fee Model to everything
                SetSecurityInitializer(security =>
                {
                    if (security.Type == SecurityType.Future)
                    {
                        //security.SetFeeModel(new InteractiveBrokersFeeModel());
                        security.SetFeeModel(new SaxoFeeModel());
                    }
                });

                _activeContractSymbol = future.Symbol;
            }

            _minuteConsolidator = new TickConsolidator(TimeSpan.FromMinutes(1));
            _minuteConsolidator.DataConsolidated += OnMinuteBar;
            SubscriptionManager.AddConsolidator(_activeContractSymbol, _minuteConsolidator);

            _priceWindow = new RollingWindow<TradeBar>(LookbackMinutes);

            SetWarmUp(TimeSpan.FromMinutes(LookbackMinutes));

            Schedule.On(DateRules.EveryDay(), TimeRules.BeforeMarketOpen(symbol: _activeContractSymbol, minutesBeforeOpen: 10), () =>
            {
            });
        }

        /// <summary>
        /// This event fires once every minute, when the consolidator finishes a bar.
        /// </summary>
        private void OnMinuteBar(object sender, TradeBar bar)
        {
            // 2. Add the new 1-minute bar to the RollingWindow
            _priceWindow.Add(bar);

            // Log the bar details to verify
            //Log($"[1-Min Bar] Time: {bar.Time.ToShortTimeString()} | Close: {bar.Close} | Vol: {bar.Volume}");

            // Example Logic: Buy if the window is full and price is rising
            if (!_priceWindow.IsReady) return;

            decimal priceChange = _priceWindow[0].Close - _priceWindow[1].Close;

            if (!Portfolio.Invested && !_invested && priceChange > _entryDelta)
            {
                Log("Buy Signal: Close > Previous High");
                var entryTicket = MarketOrder(_activeSymbol, 1);
                if (entryTicket.Status == OrderStatus.Filled)
                {
                    _entryPrice = entryTicket.AverageFillPrice;
                    _invested = true;

                    // "Place a stop loss order at -10 points from the current value"
                    decimal initialStopPrice = _entryPrice - 10;
                    _stopLossTicket = StopMarketOrder(_activeSymbol, -1, initialStopPrice);

                    Debug($"Entered Long at {_entryPrice}. Initial Stop at {initialStopPrice}");
                }
            }

            // -----------------------------------------------------------
            // EXIT / TRAILING STOP LOGIC
            // -----------------------------------------------------------
            if (Portfolio.Invested && _stopLossTicket != null && _stopLossTicket.Status == OrderStatus.Submitted)
            {
                decimal currentPrice = bar.Close;

                // Requirement: "Trailing stop distance of 2" AND "5 points higher as minimum value"

                // Calculate where the trailing stop 'wants' to be (Current Price - 2)
                decimal proposedStopPrice = currentPrice - _trailingStopDelta;

                // Calculate the "Minimum Value" requirement (Entry + 5)
                // The stop cannot be placed/moved until the logic yields a price > Entry + 5
                decimal activationPrice = _entryPrice + _activiationPriceDelta;

                // Check if our proposed trailing stop meets the minimum profit requirement
                if (proposedStopPrice >= activationPrice)
                {
                    // We only update if the new stop price is HIGHER than the old one 
                    // (Standard trailing stop behavior: never move a stop down)
                    if (proposedStopPrice > _stopLossTicket.Get(OrderField.StopPrice))
                    {
                        // Update the existing Stop Market Order
                        var updateSettings = new UpdateOrderFields
                        {
                            StopPrice = proposedStopPrice,
                            Tag = $"Trailing Triggered! Locked in > 5 pts. New Stop: {proposedStopPrice}"
                        };

                        _stopLossTicket.Update(updateSettings);
                        Debug($"Stop Updated to {proposedStopPrice}");
                    }

                }
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            // Reset state if we are stopped out or sell
            if (orderEvent.Status == OrderStatus.Filled)
            {
                if (orderEvent.Direction == OrderDirection.Sell)
                {
                    _invested = false;
                    _stopLossTicket = null;
                    Debug($"Position Closed. at price {orderEvent.FillPrice}");
                }
            }
        }

        public override void OnData(Slice slice)
        {
            if (IsWarmingUp) return;

            /*
            // 3. Check if our specific symbol has tick data in this slice
            if (data.Ticks.ContainsKey(_activeContractSymbol))
            {
                // data.Ticks[_symbol] returns a list of ticks (there can be multiple per split second)
                var ticks = data.Ticks[_activeContractSymbol];

                foreach (var tick in ticks)
                {
                    // 4. Print the data
                    // We distinguish between 'Trade' (actual execution) and 'Quote' (bid/ask update)
                    if (tick.TickType == TickType.Trade)
                    {
                        //Log($"TRADE >> Time: {tick.Time:HH:mm:ss.fff} | Price: {tick.Price} | Size: {tick.Quantity}");
                    }
                    else if (tick.TickType == TickType.Quote)
                    {
                        // Quote ticks contain Bid/Ask info
                        //Log($"QUOTE >> Time: {tick.Time:HH:mm:ss.fff} | Bid: {tick.BidPrice} x {tick.BidSize} | Ask: {tick.AskPrice} x {tick.AskSize}");
                    }
                }
            }*/

            // Now check IsReady
            /*if (_fast.IsReady)
            {
                Plot("My Chart", _fast, _slow);
            }*/

            /*

            if (IsWarmingUp) return;*/

            FuturesContract contract = null;

            foreach (var chain in slice.FutureChains)
            {
                // find the front contract expiring no earlier than in 90 days
                contract = (
                    from futuresContract in chain.Value.OrderBy(x => x.Expiry)
                    where futuresContract.Expiry < Time.Date.AddDays(90)
                    select futuresContract
                    ).FirstOrDefault();
            }
            
            // if not found, trade it
            if (contract == null)
            {
                return;
            }

            _activeSymbol = contract.Symbol;

            /*
            // Get the current price bar (e.g., the minute bar) for the active contract
            if (slice.Bars.TryGetValue(_futureSymbol, out var bar))
            {
                _priceWindow.Add(bar.Close);
            }

            if (!_priceWindow.IsReady) return;

            var currentPrice = _priceWindow[0];
            var oldPrice = _priceWindow[LookbackMinutes - 1];
            var priceChange = currentPrice - oldPrice;

            // 2. Check for an existing position (to avoid re-entering)
            var holding = Portfolio[_futureSymbol];

            // Momentum BUY Signal: Strong upward movement and no current long position
            if (priceChange > MomentumThreshold && !holding.IsLong)
            {
                // Close any existing short position first
                if (holding.IsShort) Liquidate(_futureSymbol);

                // Enter a new long position (e.g., 1 contract)
                // Use the MarketOrder function
                MarketOrder(_futureSymbol, 1);
            }

            // Momentum SELL Signal: Strong downward movement and no current short position
            else if (priceChange < -MomentumThreshold && !holding.IsShort)
            {
                // Close any existing long position first
                if (holding.IsLong) Liquidate(_futureSymbol);

                // Enter a new short position (e.g., -1 contract)
                MarketOrder(_futureSymbol, -1);
            }*/
        }

        // --- Helper Logic to find DAX/TecDAX Expiry ---
        // DAX/TecDAX futures expire on the 3rd Friday of March, June, September, December.
        private DateTime GetNextQuarterlyExpiry(DateTime currentInfo)
        {
            // Start looking from the current month
            var candidateDate = currentInfo;

            // Loop until we find a valid expiry in the future
            while (true)
            {
                // Move to next month if current month is not a quarter month (3, 6, 9, 12)
                // OR if we passed the 3rd Friday of this month
                if (!IsQuarterMonth(candidateDate.Month) || candidateDate.Date > GetThirdFriday(candidateDate.Year, candidateDate.Month))
                {
                    candidateDate = candidateDate.AddMonths(1);
                    // Reset to day 1 to avoid day-clamping issues
                    candidateDate = new DateTime(candidateDate.Year, candidateDate.Month, 1);
                    continue;
                }

                // If we are here, candidateDate.Month is 3, 6, 9, or 12
                // Return the 3rd Friday of this month
                return GetThirdFriday(candidateDate.Year, candidateDate.Month);
            }
        }

        private bool IsQuarterMonth(int month)
        {
            return month == 3 || month == 6 || month == 9 || month == 12;
        }

        private DateTime GetThirdFriday(int year, int month)
        {
            DateTime firstDay = new DateTime(year, month, 1);
            int dayOfWeek = (int)firstDay.DayOfWeek;
            int daysUntilFriday = (DayOfWeek.Friday - firstDay.DayOfWeek + 7) % 7;

            // First Friday is daysUntilFriday + 1. 
            // 3rd Friday is + 14 days after that.
            return firstDay.AddDays(daysUntilFriday + 14);
        }
    }
}
