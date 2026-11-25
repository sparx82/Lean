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
    /// Minimal benchmark: hold N contracts of the front month, roll, do nothing else.
    ///
    /// PURPOSE
    /// Strategy backtests on this contract kept disagreeing with the offline research, and the
    /// disagreement showed up even for plain buy and hold - which has no signal, no parameters
    /// and nothing to get wrong except holding and rolling. So this file isolates exactly that.
    ///
    /// SELF-CHECK
    /// While holding, the algorithm accumulates the theoretical roll-adjusted P&L itself: the
    /// sum of within-contract 17:30 close-to-close changes, which is by construction the P&L of
    /// a continuously held futures position. At the end it reconciles that number against what
    /// Lean's portfolio actually did, year by year, so any divergence is localised in time
    /// instead of appearing as one unexplained total.
    ///
    /// REFERENCE (verified against the raw minute files, 2020-10-05 .. 2025-10-03)
    ///   raw front-month price change   +2243 points
    ///   sum of roll gaps               -1979 points   (~99 per roll = the SMI dividend yield)
    ///   roll-adjusted chain            +4222 points   = CHF 126'660 on 3 contracts
    ///
    /// If the reconciliation below does not land within about 1% of that, no strategy built on
    /// this machinery means anything, and the gap has to be found before going further.
    /// </summary>
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="benchmark" />
    public class FutureHoldBenchmark_Oliver : QCAlgorithm
    {
        private string _root;
        private DateTime _startDate;
        private DateTime _endDate;
        private TimeSpan _sessionOpen;
        private TimeSpan _sessionEnd;
        private TimeSpan _sessionClose;
        private int _contracts;
        private int _rollDaysBeforeExpiry;

        private Future _future;
        private Symbol _tradedSymbol;
        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators
            = new Dictionary<Symbol, IDataConsolidator>();

        private decimal _multiplier = 10m;

        // theoretical chain
        private decimal _theoreticalPoints;
        private decimal _previousClose;
        private Symbol _previousContract;
        private DateTime _sessionDate = DateTime.MinValue;
        private decimal _sessionClosePrice;

        // bookkeeping
        private decimal _startingEquity;
        private int _rolls;
        private int _sessions;
        private DateTime _firstSession = DateTime.MinValue;
        private readonly List<string> _rollLog = new List<string>();

        // year-by-year reconciliation
        private int _currentYear;
        private decimal _yearStartEquity;
        private decimal _yearStartPoints;
        private readonly List<string> _yearLog = new List<string>();

        public override void Initialize()
        {
            SetAccountCurrency(GetParameter("accountCurrency", "CHF"));
            SetCash(GetParameter("startingCash", 200000));
            SetTimeZone("Europe/Zurich");
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);

            _root = GetParameter("root", Futures.Indices.SMI);
            _startDate = ParseDate(GetParameter("startDate", "20201005"), new DateTime(2020, 10, 5));
            _endDate = ParseDate(GetParameter("endDate", "20251003"), new DateTime(2025, 10, 3));
            _sessionOpen = ParseTime(GetParameter("sessionOpen", "08:00"));
            _sessionEnd = ParseTime(GetParameter("sessionEnd", "22:00"));
            _sessionClose = ParseTime(GetParameter("sessionClose", "17:30"));
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

            // No slippage model on purpose: Lean already fills buys above and sells below the
            // traded price, so adding one charges the spread twice.
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
            Log($"Hold benchmark: {_contracts} contract(s) of {_root}, roll at expiry - " +
                $"{_rollDaysBeforeExpiry} days, session {_sessionOpen}-{_sessionEnd}");
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
            if (timeOfDay < _sessionOpen || timeOfDay > _sessionEnd) return;

            var front = ResolveFrontContract(bar.EndTime);
            if (front == null) return;

            if (front != _tradedSymbol)
            {
                Roll(front, bar);
            }

            if (bar.Symbol != _tradedSymbol) return;

            if (timeOfDay <= _sessionClose) _sessionClosePrice = bar.Close;

            if (timeOfDay >= _sessionClose && _sessionDate != bar.EndTime.Date)
            {
                _sessionDate = bar.EndTime.Date;
                OnSessionClose();
            }
        }

        private void Roll(Symbol front, TradeBar bar)
        {
            var previous = _tradedSymbol;

            if (previous != null && Portfolio.ContainsKey(previous) && Portfolio[previous].Invested)
            {
                var oldPrice = Securities[previous].Price;
                var newPrice = Securities.ContainsKey(front) ? Securities[front].Price : 0m;

                Liquidate(previous, tag: "roll out");
                MarketOrder(front, _contracts, tag: "roll in");
                _rolls++;

                if (_rollLog.Count < 25)
                {
                    _rollLog.Add($"   {bar.EndTime:yyyy-MM-dd HH:mm}  {previous.Value} @ {oldPrice:F0}" +
                                 $"  ->  {front.Value} @ {newPrice:F0}   gap {newPrice - oldPrice:+0;-0}");
                }
            }

            _tradedSymbol = front;
            _previousContract = null;   // chain must skip the gap
        }

        private void OnSessionClose()
        {
            if (_sessionClosePrice <= 0) return;
            _sessions++;
            if (_firstSession == DateTime.MinValue) _firstSession = _sessionDate;

            // year boundary reconciliation
            if (_sessionDate.Year != _currentYear)
            {
                var equityDelta = Portfolio.TotalPortfolioValue - _yearStartEquity;
                var pointsDelta = _theoreticalPoints - _yearStartPoints;
                var theoretical = pointsDelta * _multiplier * _contracts;
                _yearLog.Add($"   {_currentYear}   theoretisch {theoretical,10:N0}   " +
                             $"Lean {equityDelta,10:N0}   Differenz {equityDelta - theoretical,9:N0}");
                _currentYear = _sessionDate.Year;
                _yearStartEquity = Portfolio.TotalPortfolioValue;
                _yearStartPoints = _theoreticalPoints;
            }

            // extend the theoretical chain, but only for a day we were actually holding
            if (_previousContract == _tradedSymbol && Portfolio[_tradedSymbol].Invested)
            {
                _theoreticalPoints += _sessionClosePrice - _previousClose;
            }
            _previousClose = _sessionClosePrice;
            _previousContract = _tradedSymbol;

            // make sure we hold exactly _contracts
            var held = Portfolio.ContainsKey(_tradedSymbol)
                ? (int)Portfolio[_tradedSymbol].Quantity
                : 0;
            if (held != _contracts)
            {
                MarketOrder(_tradedSymbol, _contracts - held, tag: "top up to target");
            }
        }

        public override void OnEndOfAlgorithm()
        {
            var equityDelta = Portfolio.TotalPortfolioValue - _yearStartEquity;
            var theoretical = (_theoreticalPoints - _yearStartPoints) * _multiplier * _contracts;
            _yearLog.Add($"   {_currentYear}   theoretisch {theoretical,10:N0}   " +
                         $"Lean {equityDelta,10:N0}   Differenz {equityDelta - theoretical,9:N0}");

            var actual = Portfolio.TotalPortfolioValue - _startingEquity;
            var expected = _theoreticalPoints * _multiplier * _contracts;
            var fees = TradeBuilder.ClosedTrades.Sum(t => t.TotalFees);

            Log("================ ABGLEICH ================");
            Log($"Erste Session      : {_firstSession:yyyy-MM-dd}   Sessions: {_sessions}");
            Log($"Rolls              : {_rolls}");
            Log($"Kettenpunkte       : {_theoreticalPoints:F0}");
            Log($"-> theoretisch     : {expected:N0} CHF  ({_contracts} Kontrakte)");
            Log($"Lean tatsaechlich  : {actual:N0} CHF");
            Log($"Gebuehren          : {fees:N0} CHF");
            Log($"DIFFERENZ          : {actual - expected:N0} CHF" +
                (expected != 0 ? $"  ({(actual - expected) / Math.Abs(expected) * 100:F1}%)" : ""));
            Log($"pro Roll           : {(_rolls > 0 ? (actual - expected) / _rolls : 0):N0} CHF");
            Log("---------------- pro Jahr ----------------");
            foreach (var line in _yearLog) Log(line);
            Log("---------------- Rolls -------------------");
            foreach (var line in _rollLog) Log(line);
            Log("==========================================");
        }
    }
}
