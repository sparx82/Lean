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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Slippage;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// SMI overnight strength. RESULT: a real but NOT tradeable effect. Read the numbers
    /// below before using any of this - the honest verdict is at the bottom.
    ///
    /// WHY THIS AND NOT AN INTRADAY SYSTEM
    /// Decomposing 3647 SMI sessions (2011-2025) into the two halves of the day:
    ///
    ///   overnight  17:30 -> 09:00   +1.96 pts/day   t = +2.10   54.1% positive
    ///   intraday   09:00 -> 17:30   +0.38 pts/day   t = +0.33   53.0% positive
    ///
    /// All of this contract's drift sits in the overnight window. Six intraday signal
    /// families tested on the same data were all negative, which is consistent with there
    /// being nothing to find in the 09:00-17:30 session.
    ///
    /// THE RULE
    /// Buy at the 17:30 close, sell the next morning, but only after days that close STRONG
    /// (close in the upper third of the 09:00-17:30 range). That hits 40% of nights and
    /// roughly doubles the raw overnight return:
    ///
    ///   all nights           +1.96 pts raw
    ///   close in top third   +4.52 pts raw     out of sample 2018-2021 and 2022-2025 both
    ///                                          positive, 12 of 15 calendar years positive
    ///
    /// WHY THE RAW NUMBER IS NOT THE TRADEABLE NUMBER
    /// Two execution effects, both measured rather than assumed:
    ///
    /// 1. Exit timing. The edge decays violently in the first minute of the session:
    ///      09:00 open +4.79 raw | +1 minute +3.13 | 09:01 +2.65 | 09:15 +3.76 | 09:30 +4.33
    ///    1.66 points of the raw edge IS the opening print, where the spread is ~5 points.
    ///    That part cannot be captured. Hence the 09:30 exit, in the middle of the plateau.
    ///
    /// 2. Order type. Measured over 95 trades against the raw files, a market order placed on
    ///    a bar boundary loses 4.32 pts round trip (-1.61 entering, -2.71 exiting): the spread
    ///    plus a systematically adverse one-minute fill delay.
    ///
    /// WHAT LEAN ACTUALLY PRODUCES, 2011-2025, one contract, IB fees, 1 pt slippage per side
    ///
    ///   market orders   1521 trades   +0.59 pts/trade   +604 CHF/year   hit rate 50.4%
    ///   limit orders    1283 trades   +0.99 pts/trade   +859 CHF/year   hit rate 50.5%
    ///                                 (238 entries never filled, 163 exits had to cross)
    ///
    /// VERDICT - DO NOT TRADE THIS
    /// CHF 859 per year against a CHF 16'100 maximum drawdown, a worst night of -259 points
    /// (CHF 2'590) that no stop can prevent because the market is shut, and a negative Sharpe
    /// ratio. The statistical effect is real; the tradeable remainder after costs is not worth
    /// the tail risk. The limit-order figure is also an upper bound - Lean fills a limit as
    /// soon as price touches it and ignores queue position, which on a contract doing ~9'400
    /// trades a day is exactly what decides whether you get filled.
    ///
    /// This file is kept as the measurement, not as a strategy.
    /// </summary>
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="overnight" />
    /// <meta name="tag" content="seasonality" />
    public class FutureOvernightAlgorithm_Oliver : QCAlgorithm
    {
        #region Configuration

        private string _root;
        private DateTime _startDate;
        private DateTime _endDate;

        private TimeSpan _sessionStart;      // session open, anchors the range
        private TimeSpan _entryTime;         // session close, we buy here
        private TimeSpan _exitTime;          // next morning, we sell here

        private decimal _closePositionMin;   // close must sit this high in the day's range
        private int _contracts;
        private decimal _maxNotionalFraction; // cap position vs portfolio value
        private decimal _protectiveStopPoints; // 0 = no stop (market is shut most of the night)
        private int _rollDaysBeforeExpiry;
        private decimal _slippagePoints;
        private bool _useLimitOrders;     // post at the bar close instead of paying the spread
        private int _entryTimeoutMinutes; // give up on the night if the entry does not fill
        private int _exitTimeoutMinutes;  // then cross the spread, we must not stay long

        #endregion

        #region State

        private Future _future;
        private Symbol _tradedSymbol;
        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators
            = new Dictionary<Symbol, IDataConsolidator>();

        private decimal _tickSize = 1m;
        private decimal _multiplier = 10m;

        // running 09:00-17:30 session bar
        private DateTime _sessionDate = DateTime.MinValue;
        private decimal _sessionOpen;
        private decimal _sessionHigh;
        private decimal _sessionLow;
        private decimal _sessionClose;
        private bool _sessionValid;

        private bool _holding;
        private decimal _entryPrice;
        private DateTime _entryTimeStamp;
        private OrderTicket _stopTicket;

        private OrderTicket _entryTicket;   // resting entry limit
        private OrderTicket _exitTicket;    // resting exit limit
        private DateTime _entryPostedAt;
        private DateTime _exitPostedAt;
        private int _entriesMissed;
        private int _exitsCrossed;

        // statistics
        private int _nightsEvaluated;
        private int _nightsTaken;
        private decimal _grossPoints;
        private decimal _worstNightPoints;

        #endregion

        public override void Initialize()
        {
            SetAccountCurrency(GetParameter("accountCurrency", "CHF"));
            SetCash(GetParameter("startingCash", 100000));
            SetTimeZone("Europe/Zurich");
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);

            LoadParameters();

            SetStartDate(_startDate.Year, _startDate.Month, _startDate.Day);
            SetEndDate(_endDate.Year, _endDate.Month, _endDate.Day);

            _future = AddFuture(
                _root,
                Resolution.Minute,
                Market.EUREX,
                dataMappingMode: DataMappingMode.OpenInterest,
                dataNormalizationMode: DataNormalizationMode.BackwardsRatio,
                contractDepthOffset: 0);

            _future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(182));

            SetSecurityInitializer(security =>
            {
                if (security.Type != SecurityType.Future) return;
                security.SetFeeModel(new InteractiveBrokersFeeModel());
                security.SetSlippageModel(new FixedPointSlippageModel(_slippagePoints));
            });

            _tickSize = _future.SymbolProperties.MinimumPriceVariation;
            _multiplier = _future.SymbolProperties.ContractMultiplier;

            SetBenchmark(_future.Symbol);

            Log($"Overnight strength on {_root}: enter {_entryTime}, exit {_exitTime}, " +
                $"close position >= {_closePositionMin:P0}, {_contracts} contract(s), " +
                $"tick={_tickSize} multiplier={_multiplier}");
        }

        private void LoadParameters()
        {
            _root = GetParameter("root", Futures.Indices.SMI);
            _startDate = ParseDate(GetParameter("startDate", "20110103"), new DateTime(2011, 1, 3));
            _endDate = ParseDate(GetParameter("endDate", "20251003"), new DateTime(2025, 10, 3));

            _sessionStart = ParseTime(GetParameter("sessionStart", "09:00"));
            _entryTime = ParseTime(GetParameter("entryTime", "17:30"));
            // NOT 09:00. Measured decay of the edge after the open (1463 nights):
            //   09:00 open  +2.79 net | 09:00+1min +1.13 | 09:01 +0.65 | 09:05 +0.89
            //   09:15 +1.76 | 09:30 +2.33 (t=+3.34) | 10:00 +2.31
            // 1.66 points of the raw edge is the opening print itself, where the spread is
            // widest (08h median 5 points) - that part is not capturable. What remains is a
            // flat plateau from 09:15 to 10:00, so the exit sits in the middle of it.
            _exitTime = ParseTime(GetParameter("exitTime", "09:30"));

            // 0 takes every night, which reproduces the unfiltered +1.96 gross / -0.04 net.
            _closePositionMin = GetParameter("closePositionMin", 0.66m);
            _contracts = GetParameter("contracts", 1);
            _maxNotionalFraction = GetParameter("maxNotionalFraction", 2.0m);
            _protectiveStopPoints = GetParameter("protectiveStopPoints", 0m);
            _rollDaysBeforeExpiry = GetParameter("rollDaysBeforeExpiry", 3);
            _slippagePoints = GetParameter("slippagePoints", 1.0m);

            // A market order submitted on a bar boundary fills a minute later, and the drift
            // measured over 95 trades is adverse on both legs: -1.61 pts entering, -2.71 pts
            // exiting, on top of the 1-point-per-side spread. Posting passively removes the
            // timing component.
            _useLimitOrders = GetParameter("useLimitOrders", 1) == 1;
            _entryTimeoutMinutes = GetParameter("entryTimeoutMinutes", 5);
            _exitTimeoutMinutes = GetParameter("exitTimeoutMinutes", 15);
        }

        private static DateTime ParseDate(string value, DateTime fallback)
        {
            DateTime parsed;
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed) ? parsed : fallback;
        }

        private static TimeSpan ParseTime(string value)
        {
            TimeSpan parsed;
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out parsed)
                ? parsed : TimeSpan.Zero;
        }

        public override void OnData(Slice slice)
        {
        }

        /// <summary>
        /// One minute consolidator per contract. Data arrives under the individual contract
        /// symbols, never under the canonical symbol.
        /// </summary>
        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                var symbol = security.Symbol;
                if (symbol.SecurityType != SecurityType.Future || symbol.IsCanonical()) continue;
                if (_consolidators.ContainsKey(symbol)) continue;

                var consolidator = new TradeBarConsolidator(TimeSpan.FromMinutes(1));
                consolidator.DataConsolidated += OnMinute;
                SubscriptionManager.AddConsolidator(symbol, consolidator);
                _consolidators[symbol] = consolidator;
            }

            foreach (var security in changes.RemovedSecurities)
            {
                IDataConsolidator consolidator;
                if (!_consolidators.TryGetValue(security.Symbol, out consolidator)) continue;

                SubscriptionManager.RemoveConsolidator(security.Symbol, consolidator);
                var bar = consolidator as TradeBarConsolidator;
                if (bar != null) bar.DataConsolidated -= OnMinute;
                _consolidators.Remove(security.Symbol);
            }
        }

        /// <summary>
        /// Front contract = nearest expiry that is still more than _rollDaysBeforeExpiry away.
        /// Future.Mapped is deliberately not used: it stays empty whenever the continuous
        /// mapping cannot be resolved from the local data folder.
        /// </summary>
        private Symbol ResolveFrontContract(DateTime now)
        {
            Symbol front = null;
            foreach (var symbol in _consolidators.Keys)
            {
                if ((symbol.ID.Date.Date - now.Date).TotalDays <= _rollDaysBeforeExpiry) continue;
                if (front == null || symbol.ID.Date < front.ID.Date) front = symbol;
            }
            return front;
        }

        private void OnMinute(object sender, TradeBar bar)
        {
            var front = ResolveFrontContract(bar.EndTime);
            if (front == null) return;

            if (front != _tradedSymbol)
            {
                // Never carry an overnight position across a roll.
                if (_holding) ForceExit(bar, "roll");
                _tradedSymbol = front;
                _sessionValid = false;
            }

            if (bar.Symbol != _tradedSymbol) return;

            var timeOfDay = bar.EndTime.TimeOfDay;

            ManageRestingOrders(bar);

            // 1) morning exit comes first, before the new session bar starts building
            if (_holding && _exitTicket == null
                && bar.EndTime.Date > _entryTimeStamp.Date && timeOfDay >= _exitTime)
            {
                ExitPosition(bar, "morning exit");
            }

            // 2) maintain the 09:00-17:30 session bar
            if (timeOfDay >= _sessionStart && timeOfDay <= _entryTime)
            {
                if (bar.EndTime.Date != _sessionDate)
                {
                    _sessionDate = bar.EndTime.Date;
                    _sessionOpen = bar.Open;
                    _sessionHigh = bar.High;
                    _sessionLow = bar.Low;
                    _sessionValid = true;
                }
                else
                {
                    _sessionHigh = Math.Max(_sessionHigh, bar.High);
                    _sessionLow = Math.Min(_sessionLow, bar.Low);
                }
                _sessionClose = bar.Close;
            }

            // 3) evening entry
            if (!_holding && _sessionValid && timeOfDay >= _entryTime
                && bar.EndTime.Date == _sessionDate)
            {
                EvaluateEntry(bar);
            }
        }

        /// <summary>
        /// Times out the resting limit orders. An unfilled entry simply skips the night; an
        /// unfilled exit has to cross the spread, because carrying the position another day
        /// is a different bet from the one that was tested.
        /// </summary>
        private void ManageRestingOrders(TradeBar bar)
        {
            if (_entryTicket != null)
            {
                if (_entryTicket.Status == OrderStatus.Filled)
                {
                    _entryPrice = _entryTicket.AverageFillPrice;
                    _entryTimeStamp = _entryPostedAt;
                    _holding = true;
                    _nightsTaken++;
                    _entryTicket = null;
                }
                else if (!_entryTicket.Status.IsOpen())
                {
                    _entryTicket = null;
                }
                else if ((bar.EndTime - _entryPostedAt).TotalMinutes >= _entryTimeoutMinutes)
                {
                    _entryTicket.Cancel("entry limit not filled");
                    _entryTicket = null;
                    _entriesMissed++;
                }
            }

            if (_exitTicket != null)
            {
                if (_exitTicket.Status == OrderStatus.Filled)
                {
                    BookExit(_exitTicket.AverageFillPrice);
                    _exitTicket = null;
                }
                else if (!_exitTicket.Status.IsOpen())
                {
                    _exitTicket = null;
                }
                else if ((bar.EndTime - _exitPostedAt).TotalMinutes >= _exitTimeoutMinutes)
                {
                    _exitTicket.Cancel("exit limit not filled");
                    _exitTicket = null;
                    _exitsCrossed++;
                    ForceExit(bar, "exit timeout");
                }
            }
        }

        private void BookExit(decimal exitPrice)
        {
            var points = exitPrice - _entryPrice;
            _grossPoints += points;
            _worstNightPoints = Math.Min(_worstNightPoints, points);
            _holding = false;

            if (_stopTicket != null && _stopTicket.Status.IsOpen()) _stopTicket.Cancel();
            _stopTicket = null;
        }

        private void ForceExit(TradeBar bar, string reason)
        {
            if (Portfolio.ContainsKey(_tradedSymbol) && Portfolio[_tradedSymbol].Invested)
            {
                Liquidate(_tradedSymbol, tag: reason);
            }
            BookExit(bar.Close);
        }

        private void EvaluateEntry(TradeBar bar)
        {
            _sessionValid = false;   // one decision per session, whatever the outcome
            _nightsEvaluated++;

            var range = _sessionHigh - _sessionLow;
            if (range <= 0) return;

            var closePosition = (_sessionClose - _sessionLow) / range;
            if (closePosition < _closePositionMin) return;

            // Do not open a position that the roll would have to close in the morning.
            if ((_tradedSymbol.ID.Date.Date - bar.EndTime.Date).TotalDays <= _rollDaysBeforeExpiry + 1)
            {
                return;
            }

            var quantity = PositionSize(bar.Close);
            if (quantity <= 0) return;

            var tag = $"overnight long, close at {closePosition:P0} of range";

            if (_useLimitOrders)
            {
                // Join the close rather than lifting the offer a minute later.
                _entryTicket = LimitOrder(_tradedSymbol, quantity, RoundToTick(bar.Close), tag: tag);
                _entryPostedAt = bar.EndTime;
                return;   // ManageRestingOrders promotes this to a position once it fills
            }

            var ticket = MarketOrder(_tradedSymbol, quantity, tag: tag);
            if (ticket == null || ticket.Status == OrderStatus.Invalid) return;

            _holding = true;
            _entryPrice = ticket.AverageFillPrice > 0 ? ticket.AverageFillPrice : bar.Close;
            _entryTimeStamp = bar.EndTime;
            _nightsTaken++;

            if (_protectiveStopPoints > 0)
            {
                // Only effective while the exchange is open - it cannot protect the 22:00-08:00 gap.
                var stop = RoundToTick(_entryPrice - _protectiveStopPoints);
                _stopTicket = StopMarketOrder(_tradedSymbol, -quantity, stop, tag: "overnight stop");
            }
        }

        private void ExitPosition(TradeBar bar, string reason)
        {
            if (!Portfolio.ContainsKey(_tradedSymbol) || !Portfolio[_tradedSymbol].Invested)
            {
                _holding = false;
                return;
            }

            if (_useLimitOrders)
            {
                var quantity = Portfolio[_tradedSymbol].Quantity;
                _exitTicket = LimitOrder(_tradedSymbol, -quantity, RoundToTick(bar.Close),
                    tag: reason);
                _exitPostedAt = bar.EndTime;
                return;
            }

            ForceExit(bar, reason);
        }

        private int PositionSize(decimal price)
        {
            var notionalPerContract = price * _multiplier;
            if (notionalPerContract <= 0) return 0;

            var cap = (int)Math.Floor(Portfolio.TotalPortfolioValue * _maxNotionalFraction
                                      / notionalPerContract);
            return Math.Max(0, Math.Min(_contracts, cap));
        }

        private decimal RoundToTick(decimal price)
        {
            if (_tickSize <= 0) return price;
            return Math.Round(price / _tickSize, MidpointRounding.AwayFromZero) * _tickSize;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled) return;
            Log($"FILL {orderEvent.UtcTime:yyyy-MM-dd HH:mm} {orderEvent.Direction} " +
                $"qty={orderEvent.FillQuantity} price={orderEvent.FillPrice:F2}");
        }

        public override void OnEndOfAlgorithm()
        {
            var closed = TradeBuilder.ClosedTrades;
            Log("==================== SUMMARY ====================");
            Log($"Nights evaluated : {_nightsEvaluated}");
            Log($"Nights taken     : {_nightsTaken} " +
                $"({(_nightsEvaluated > 0 ? 100m * _nightsTaken / _nightsEvaluated : 0):F0}%)");

            if (closed.Count == 0)
            {
                Log("No closed trades.");
                Log("=================================================");
                return;
            }

            var gross = closed.Sum(t => t.ProfitLoss);
            var fees = closed.Sum(t => t.TotalFees);
            var winners = closed.Count(t => t.ProfitLoss > 0);
            var years = (EndDate - StartDate).TotalDays / 365.25;

            Log($"Closed trades    : {closed.Count} (win {winners}, " +
                $"hit rate {100m * winners / closed.Count:F1}%)");
            Log($"Gross P/L        : {gross:F0} CHF");
            Log($"Fees             : {fees:F0} CHF");
            Log($"Net P/L          : {gross - fees:F0} CHF  " +
                $"({(gross - fees) / (decimal)Math.Max(years, 0.01):F0} CHF per year)");
            Log($"Per trade        : {(gross - fees) / closed.Count:F2} CHF " +
                $"({(gross - fees) / closed.Count / _multiplier:F2} index points)");
            Log($"Worst night      : {_worstNightPoints:F0} points");
            Log($"Entries missed   : {_entriesMissed} (limit never filled)");
            Log($"Exits crossed    : {_exitsCrossed} (limit timed out, had to pay the spread)");
            Log("=================================================");
        }

        /// <summary>
        /// Charges a fixed number of index points per side, matching the SMI's 2-point median
        /// spread (1 point per side).
        /// </summary>
        private class FixedPointSlippageModel : ISlippageModel
        {
            private readonly decimal _points;

            public FixedPointSlippageModel(decimal points)
            {
                _points = points;
            }

            public decimal GetSlippageApproximation(Security asset, Order order)
            {
                return _points;
            }
        }
    }
}
