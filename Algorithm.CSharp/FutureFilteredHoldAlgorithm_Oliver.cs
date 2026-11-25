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
using QuantConnect.Securities;
using QuantConnect.Securities.Future;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Filtered buy and hold on the SMI future.
    ///
    /// THE RULE
    /// Hold N contracts from one 17:30 close to the next, but only after a day that closed
    /// STRONG - with the 17:30 close in the upper part of that day's 09:00-17:30 range. After a
    /// weak close, stand aside for one day. A run of strong closes keeps the position on; a
    /// round trip is only paid when the filter switches off.
    ///
    /// Setting closePositionMin to 0 makes the filter always true, which turns this file into
    /// the plain buy-and-hold benchmark - same comparison, same machinery, no implementation
    /// difference to argue about.
    ///
    /// WHAT IT ACTUALLY DELIVERS (walk-forward, 2014-2025, 3 contracts, 2.2 pts per round trip)
    /// The threshold is re-selected at the start of every year using only the years before it,
    /// then applied blind:
    ///
    ///   buy and hold                     +20'554 /yr   Sharpe 0.48   max DD -90'630
    ///   walk-forward, Sharpe criterion   +14'602 /yr   Sharpe 0.96   max DD -46'050
    ///   walk-forward, return criterion   +21'936 /yr   Sharpe 0.78   max DD -61'260
    ///
    /// Read that honestly: ON RETURN THIS DOES NOT RELIABLY BEAT THE INDEX. The return
    /// criterion is 7% ahead, which is noise; the Sharpe criterion is 29% behind. What it does
    /// deliver, consistently, is roughly double the Sharpe ratio at roughly half the drawdown.
    ///
    /// A WARNING ABOUT AN EARLIER VERSION OF THIS COMMENT
    /// This file first claimed +30'254 per year at a fixed threshold of 0.40. That number was
    /// contaminated: 0.40 had been chosen by looking at the entire sample, including the years
    /// it was then scored on. Removing that hindsight costs about CHF 5'100 per year. The
    /// in-sample figures for a fixed 0.40 (+27'051/yr, Sharpe 1.02 over 2014-2025) are kept
    /// here only as the measure of how much hindsight was worth - they are not attainable.
    ///
    /// CHOOSING THE THRESHOLD
    /// The default is 0.80 because the Sharpe criterion selected it in ALL TWELVE walk-forward
    /// years without a single change - a parameter that survives twelve independent decisions
    /// unchanged is stable. The return criterion picks 0.40 in 9 of 12 years and earns more but
    /// with a worse risk profile. Note that the choice of criterion is itself a free parameter
    /// worth about a third of the result (+14'602 vs +21'936), which is a reason for caution,
    /// not a menu to optimise over.
    ///
    /// HOW TO ACTUALLY BEAT THE INDEX WITH THIS
    /// Not through more return per contract - through more contracts at the same risk, which is
    /// what the halved drawdown buys:
    ///
    ///   walk-forward (Sharpe), 3 contracts   +14'602 /yr   max DD -46'050
    ///   walk-forward (Sharpe), 6 contracts   +29'204 /yr   max DD -92'100
    ///   buy and hold,          3 contracts   +20'554 /yr   max DD -90'630
    ///
    /// At the same drawdown, six contracts of this earn 42% more than three of buy and hold.
    /// That needs CHF 720'000 notional, CHF 42'504 initial margin, and the ability to sit
    /// through a CHF 92'000 drawdown.
    ///
    /// WHY THE MECHANICS HERE CAN BE TRUSTED
    /// Earlier attempts on this contract disagreed with the offline research by factors of 4,
    /// through four separate defects: execution on fabricated 01:11 fill-forward bars, rolling
    /// on those same bars, a warm-up that left target and actual position permanently out of
    /// sync, and a slippage model charging a spread that Lean's fills already contained. So the
    /// self-reconciliation from FutureHoldBenchmark_Oliver is kept: the algorithm accumulates
    /// its own theoretical roll-adjusted P&L and prints it against the portfolio year by year.
    /// With closePositionMin = 0 it reproduces buy and hold to within 2% of an independently
    /// verified price chain (see build_chain.py), which is what makes the strategy number above
    /// worth anything at all.
    ///
    /// RISK
    /// This holds through the close every night it is on. The tail cannot be stopped, because
    /// the market is shut: worst single day -475 points = CHF 14'250 on 3 contracts, 1% of days
    /// beyond -215 points = CHF 6'450. 2022 lost CHF 34'430 (buy and hold lost 59'064).
    /// </summary>
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="strategy example" />
    public class FutureFilteredHoldAlgorithm_Oliver : QCAlgorithm
    {
        #region Configuration

        private string _root;
        private DateTime _startDate;
        private DateTime _endDate;

        private TimeSpan _sessionOpen;     // contract really trades from here
        private TimeSpan _sessionEnd;      // ... to here
        private TimeSpan _rangeStart;      // range for the closing-strength filter starts here
        private TimeSpan _sessionClose;    // decision and execution happen on this bar

        private decimal _closePositionMin; // 0 = always in, i.e. plain buy and hold
        private int _contracts;
        private int _rollDaysBeforeExpiry;

        #endregion

        #region State

        private Future _future;
        private Symbol _tradedSymbol;
        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators
            = new Dictionary<Symbol, IDataConsolidator>();

        private decimal _multiplier = 10m;

        // running 09:00-17:30 range
        private DateTime _rangeDate = DateTime.MinValue;
        private decimal _rangeHigh;
        private decimal _rangeLow;
        private decimal _sessionClosePrice;
        private DateTime _sessionDate = DateTime.MinValue;

        // theoretical chain, for the self-check
        private decimal _theoreticalPoints;
        private decimal _previousClose;
        private Symbol _previousContract;
        private bool _heldOverWindow;

        private int _targetPosition;
        private int _entries;
        private int _exits;
        private int _rolls;
        private int _sessions;
        private int _sessionsInMarket;
        private DateTime _firstSession = DateTime.MinValue;

        private decimal _startingEquity;
        private int _currentYear;
        private decimal _yearStartEquity;
        private decimal _yearStartPoints;
        private readonly List<string> _yearLog = new List<string>();

        #endregion

        public override void Initialize()
        {
            SetAccountCurrency(GetParameter("accountCurrency", "CHF"));
            SetCash(GetParameter("startingCash", 200000));
            SetTimeZone("Europe/Zurich");
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);

            _root = GetParameter("root", Futures.Indices.SMI);
            _startDate = ParseDate(GetParameter("startDate", "20110103"), new DateTime(2011, 1, 3));
            _endDate = ParseDate(GetParameter("endDate", "20251003"), new DateTime(2025, 10, 3));

            // The contract trades 08:00-22:00. Lean's market hours database claims 01:10-22:00
            // and fill-forwards synthetic bars from 01:10 that carry a stale price; without this
            // gate the algorithm would execute in a window where no market exists.
            _sessionOpen = ParseTime(GetParameter("sessionOpen", "08:00"));
            _sessionEnd = ParseTime(GetParameter("sessionEnd", "22:00"));
            _rangeStart = ParseTime(GetParameter("rangeStart", "09:00"));
            _sessionClose = ParseTime(GetParameter("sessionClose", "17:30"));

            // 0.80 = the walk-forward selection under the Sharpe criterion, unchanged in all
            // twelve out-of-sample years. Use 0.40 for the return criterion (more money, worse
            // risk profile, less stable selection). Use 0 for the buy-and-hold benchmark.
            _closePositionMin = GetParameter("closePositionMin", 0.80m);
            _contracts = GetParameter("contracts", 3);
            _rollDaysBeforeExpiry = GetParameter("rollDaysBeforeExpiry", 3);

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

            // No slippage model: Lean already fills buys above and sells below the traded price
            // (measured: 3.93 points implied round trip), so adding one charges the spread twice.
            SetSecurityInitializer(security =>
            {
                if (security.Type == SecurityType.Future)
                {
                    security.SetFeeModel(new InteractiveBrokersFeeModel());
                }
            });

            _multiplier = _future.SymbolProperties.ContractMultiplier;
            _startingEquity = Portfolio.TotalPortfolioValue;
            _yearStartEquity = _startingEquity;
            _currentYear = _startDate.Year;

            SetBenchmark(_future.Symbol);
            Log($"Filtered hold on {_root}: {_contracts} contract(s), " +
                $"close position >= {_closePositionMin:P0} of the {_rangeStart:hh\\:mm}-" +
                $"{_sessionClose:hh\\:mm} range, decision at {_sessionClose:hh\\:mm}");
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
        /// Front contract = nearest expiry more than _rollDaysBeforeExpiry away. Future.Mapped is
        /// not used: it stays empty whenever the continuous mapping cannot be resolved from the
        /// local data folder, which freezes an algorithm on its first contract for ever.
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
            var timeOfDay = bar.EndTime.TimeOfDay;

            // Session gate first: rolling or trading on a fabricated pre-session bar executes
            // at stale prices in a market that is not open.
            if (timeOfDay < _sessionOpen || timeOfDay > _sessionEnd) return;

            var front = ResolveFrontContract(bar.EndTime);
            if (front == null) return;

            if (front != _tradedSymbol) Roll(front, bar);
            if (bar.Symbol != _tradedSymbol) return;

            // maintain the 09:00-17:30 range that the filter reads
            if (timeOfDay >= _rangeStart && timeOfDay <= _sessionClose)
            {
                if (bar.EndTime.Date != _rangeDate)
                {
                    _rangeDate = bar.EndTime.Date;
                    _rangeHigh = bar.High;
                    _rangeLow = bar.Low;
                }
                else
                {
                    _rangeHigh = Math.Max(_rangeHigh, bar.High);
                    _rangeLow = Math.Min(_rangeLow, bar.Low);
                }
                _sessionClosePrice = bar.Close;
            }

            if (timeOfDay >= _sessionClose && _sessionDate != bar.EndTime.Date
                && _rangeDate == bar.EndTime.Date)
            {
                _sessionDate = bar.EndTime.Date;
                OnSessionClose();
            }
        }

        /// <summary>
        /// Moves an existing position to the new front contract. When flat there is nothing to
        /// roll - the next decision simply opens in the new contract.
        /// </summary>
        private void Roll(Symbol front, TradeBar bar)
        {
            var previous = _tradedSymbol;

            if (previous != null && Portfolio.ContainsKey(previous) && Portfolio[previous].Invested)
            {
                var quantity = (int)Portfolio[previous].Quantity;
                Liquidate(previous, tag: "roll out");
                MarketOrder(front, quantity, tag: "roll in");
                _rolls++;
            }

            _tradedSymbol = front;
            _previousContract = null;   // the chain must never absorb a calendar spread
        }

        private void OnSessionClose()
        {
            if (_sessionClosePrice <= 0) return;
            _sessions++;
            if (_firstSession == DateTime.MinValue) _firstSession = _sessionDate;

            if (_sessionDate.Year != _currentYear)
            {
                var equityDelta = Portfolio.TotalPortfolioValue - _yearStartEquity;
                var theoretical = (_theoreticalPoints - _yearStartPoints) * _multiplier * _contracts;
                _yearLog.Add($"   {_currentYear}   theoretisch {theoretical,10:N0}   " +
                             $"Lean {equityDelta,10:N0}   Differenz {equityDelta - theoretical,9:N0}");
                _currentYear = _sessionDate.Year;
                _yearStartEquity = Portfolio.TotalPortfolioValue;
                _yearStartPoints = _theoreticalPoints;
            }

            // 1) book the window that has just ended, using the position we actually held in it
            if (_previousContract == _tradedSymbol && _heldOverWindow)
            {
                _theoreticalPoints += _sessionClosePrice - _previousClose;
                _sessionsInMarket++;
            }
            _previousClose = _sessionClosePrice;
            _previousContract = _tradedSymbol;

            // 2) decide the position for the window that starts now
            var range = _rangeHigh - _rangeLow;
            var closePosition = range > 0 ? (_sessionClosePrice - _rangeLow) / range : 0m;
            var desired = closePosition >= _closePositionMin ? _contracts : 0;

            SetTarget(desired);
            _heldOverWindow = desired > 0;
        }

        private void SetTarget(int desired)
        {
            var held = Portfolio.ContainsKey(_tradedSymbol)
                ? (int)Portfolio[_tradedSymbol].Quantity
                : 0;

            if (held == desired)
            {
                _targetPosition = desired;
                return;
            }

            if (desired > held) _entries++;
            else _exits++;

            MarketOrder(_tradedSymbol, desired - held,
                tag: $"filter -> {desired} contract(s)");
            _targetPosition = desired;
        }

        public override void OnEndOfAlgorithm()
        {
            var equityDelta = Portfolio.TotalPortfolioValue - _yearStartEquity;
            var lastTheoretical = (_theoreticalPoints - _yearStartPoints) * _multiplier * _contracts;
            _yearLog.Add($"   {_currentYear}   theoretisch {lastTheoretical,10:N0}   " +
                         $"Lean {equityDelta,10:N0}   Differenz {equityDelta - lastTheoretical,9:N0}");

            var actual = Portfolio.TotalPortfolioValue - _startingEquity;
            var expected = _theoreticalPoints * _multiplier * _contracts;
            var fees = TradeBuilder.ClosedTrades.Sum(t => t.TotalFees);
            var years = (EndDate - (_firstSession == DateTime.MinValue ? StartDate : _firstSession))
                .TotalDays / 365.25;

            Log("================ ERGEBNIS ================");
            Log($"Schwelle           : {_closePositionMin:P0}   Kontrakte: {_contracts}");
            Log($"Erste Session      : {_firstSession:yyyy-MM-dd}   Sessions: {_sessions}");
            Log($"Im Markt           : {_sessionsInMarket} Sessions " +
                $"({(_sessions > 0 ? 100m * _sessionsInMarket / _sessions : 0):F0}%)");
            Log($"Einstiege / Ausstiege: {_entries} / {_exits}   Rolls: {_rolls}");
            Log($"Gebuehren          : {fees:N0} CHF");
            Log($"Lean netto         : {actual:N0} CHF   ({actual / (decimal)Math.Max(years, 0.01):N0} pro Jahr)");
            Log("---------------- Selbstabgleich ----------");
            Log($"Kettenpunkte       : {_theoreticalPoints:F0}");
            Log($"-> theoretisch     : {expected:N0} CHF");
            Log($"DIFFERENZ          : {actual - expected:N0} CHF" +
                (expected != 0 ? $"  ({(actual - expected) / Math.Abs(expected) * 100:F1}%)" : ""));
            Log("---------------- pro Jahr ----------------");
            foreach (var line in _yearLog) Log(line);
            Log("==========================================");
        }
    }
}
