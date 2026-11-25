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
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Mechanised version of a discretionary SMI day-trading style, built from an analysis of
    /// 1'656 real round trips (2018-2026) reconstructed from broker exports and reconciled
    /// against the broker's own variation-margin bookings.
    ///
    /// WHAT THE TRADING RECORD ACTUALLY SHOWED
    ///   - the style is short-term trend following: 88% of entries went with the last 5
    ///     minutes' move, 83% with the last 15 minutes', only 59% with the last hour's
    ///   - same-day trading was profitable in 10 of 10 years (+475'000 total), positions held
    ///     overnight lost money in 10 of 10 years (-464'000)
    ///   - winners were held a median of 69 minutes, losers 928 minutes
    ///   - entering a FLAT market (15-minute move under ~5 points) gave a 39% forward hit rate
    ///     out of sample against a 51% baseline - the one entry filter that replicated
    ///   - a VWAP-distance filter looked excellent in sample (72%) and FAILED out of sample
    ///     (48%). It is deliberately NOT in this algorithm.
    ///
    /// WHAT THIS FILE THEREFORE DOES
    ///   entry   go with the 15-minute move, but only when that move is large enough relative
    ///           to current volatility to not be noise
    ///   exit    time stop near the median winner's holding time, plus a hard flat at the
    ///           session close - never overnight, which is the single strongest finding
    ///   size    3 contracts (the record's best bucket by a wide margin; a 10-contract
    ///           excursion in June 2026 lost CHF 21'110 in eleven same-day trades)
    ///
    /// EXPECTATION BEFORE TESTING
    /// Every mechanical intraday signal tested on this contract during the same research -
    /// opening-range breakout, VWAP reversion and trend, Bollinger fade, EMA/ADX crossover,
    /// a regime-switching combination, and order-flow imbalance - was negative after the
    /// SMI's 2-point spread. A 15-minute momentum rule is the same kind of signal. This file
    /// exists to measure whether the discretionary edge survives mechanisation, not because
    /// the research predicted that it would. Read the summary the algorithm prints, not the
    /// premise.
    ///
    /// MECHANICS
    /// Session gate 08:00-22:00 before anything else (Lean's market-hours database claims the
    /// EUREX future opens at 01:10 and fill-forwards synthetic bars from then), front contract
    /// resolved from expiry dates rather than Future.Mapped, and no slippage model because
    /// Lean's fills already sit on the bid/ask.
    /// </summary>
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="day trading" />
    /// <meta name="tag" content="strategy example" />
    public class FutureTraderStyleAlgorithm_Oliver : QCAlgorithm
    {
        #region Configuration

        private string _root;
        private DateTime _startDate;
        private DateTime _endDate;

        private TimeSpan _sessionOpen;
        private TimeSpan _sessionEnd;
        private TimeSpan _tradeFrom;
        private TimeSpan _tradeUntil;
        private TimeSpan _flattenAt;

        private int _barMinutes;            // signal bar size
        private int _momentumBars;          // lookback for the move, in bars
        private decimal _minMovePoints;     // absolute floor for "the market is moving"
        private decimal _minMoveVolFactor;  // ... and relative to recent volatility
        private int _maxHoldBars;           // time stop
        private decimal _stopAtr;           // 0 = no stop
        private int _contracts;
        private int _maxTradesPerDay;
        private int _rollDaysBeforeExpiry;

        #endregion

        #region State

        private Future _future;
        private Symbol _tradedSymbol;
        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators
            = new Dictionary<Symbol, IDataConsolidator>();

        private decimal _tickSize = 1m;
        private decimal _multiplier = 10m;

        private RollingWindow<decimal> _closes;
        private RollingWindow<decimal> _ranges;

        private DateTime _sessionDate = DateTime.MinValue;
        private int _tradesToday;

        private int _direction;
        private decimal _entryPrice;
        private decimal _stopPrice;
        private int _barsInTrade;
        private bool _inPosition;

        private int _signalsFlat;
        private int _signalsTaken;
        private int _exitsTime;
        private int _exitsStop;
        private int _exitsSession;
        private decimal _startingEquity;

        #endregion

        public override void Initialize()
        {
            SetAccountCurrency(GetParameter("accountCurrency", "CHF"));
            SetCash(GetParameter("startingCash", 100000));
            SetTimeZone("Europe/Zurich");
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);

            _root = GetParameter("root", Futures.Indices.SMI);
            _startDate = ParseDate(GetParameter("startDate", "20180102"), new DateTime(2018, 1, 2));
            _endDate = ParseDate(GetParameter("endDate", "20251003"), new DateTime(2025, 10, 3));

            _sessionOpen = ParseTime(GetParameter("sessionOpen", "08:00"));
            _sessionEnd = ParseTime(GetParameter("sessionEnd", "22:00"));
            _tradeFrom = ParseTime(GetParameter("tradeFrom", "09:00"));
            _tradeUntil = ParseTime(GetParameter("tradeUntil", "17:00"));
            _flattenAt = ParseTime(GetParameter("flattenAt", "17:25"));

            _barMinutes = GetParameter("barMinutes", 5);
            _momentumBars = GetParameter("momentumBars", 3);       // 3 x 5 min = 15 minutes
            _minMovePoints = GetParameter("minMovePoints", 5m);    // the replicated flat filter
            _minMoveVolFactor = GetParameter("minMoveVolFactor", 0.33m);
            _maxHoldBars = GetParameter("maxHoldBars", 14);        // ~70 min, the median winner
            _stopAtr = GetParameter("stopAtr", 0m);
            _contracts = GetParameter("contracts", 3);
            _maxTradesPerDay = GetParameter("maxTradesPerDay", 4);
            _rollDaysBeforeExpiry = GetParameter("rollDaysBeforeExpiry", 3);

            SetStartDate(_startDate.Year, _startDate.Month, _startDate.Day);
            SetEndDate(_endDate.Year, _endDate.Month, _endDate.Day);

            _future = AddFuture(_root, Resolution.Minute, Market.EUREX,
                dataMappingMode: DataMappingMode.OpenInterest,
                dataNormalizationMode: DataNormalizationMode.BackwardsRatio,
                contractDepthOffset: 0);
            _future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(182));

            // No slippage model: Lean's fills already sit on the bid/ask, so adding one would
            // charge the SMI's 2-point spread twice (measured: 3.93 points implied round trip).
            SetSecurityInitializer(security =>
            {
                if (security.Type == SecurityType.Future)
                {
                    security.SetFeeModel(new InteractiveBrokersFeeModel());
                }
            });

            _tickSize = _future.SymbolProperties.MinimumPriceVariation;
            _multiplier = _future.SymbolProperties.ContractMultiplier;

            _closes = new RollingWindow<decimal>(_momentumBars + 1);
            _ranges = new RollingWindow<decimal>(12);
            _startingEquity = Portfolio.TotalPortfolioValue;

            SetBenchmark(_future.Symbol);
            Log($"Trader-style momentum on {_root}: {_barMinutes}min bars, " +
                $"{_momentumBars * _barMinutes}min momentum, flat filter " +
                $"max({_minMovePoints} pts, {_minMoveVolFactor}x vol), " +
                $"time stop {_maxHoldBars} bars, {_contracts} contract(s)");
        }

        private static DateTime ParseDate(string v, DateTime fallback)
        {
            DateTime p;
            return DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out p) ? p : fallback;
        }

        private static TimeSpan ParseTime(string v)
        {
            TimeSpan p;
            return TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out p) ? p : TimeSpan.Zero;
        }

        public override void OnData(Slice slice) { }

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                var symbol = security.Symbol;
                if (symbol.SecurityType != SecurityType.Future || symbol.IsCanonical()) continue;
                if (_consolidators.ContainsKey(symbol)) continue;

                var consolidator = new TradeBarConsolidator(TimeSpan.FromMinutes(_barMinutes));
                consolidator.DataConsolidated += OnBar;
                SubscriptionManager.AddConsolidator(symbol, consolidator);
                _consolidators[symbol] = consolidator;
            }

            foreach (var security in changes.RemovedSecurities)
            {
                IDataConsolidator c;
                if (!_consolidators.TryGetValue(security.Symbol, out c)) continue;
                SubscriptionManager.RemoveConsolidator(security.Symbol, c);
                var bar = c as TradeBarConsolidator;
                if (bar != null) bar.DataConsolidated -= OnBar;
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

        private void OnBar(object sender, TradeBar bar)
        {
            var t = bar.EndTime.TimeOfDay;
            if (t < _sessionOpen || t > _sessionEnd) return;      // gate first, always

            var front = ResolveFrontContract(bar.EndTime);
            if (front == null) return;

            if (front != _tradedSymbol)
            {
                if (_inPosition) Exit(bar, "roll");
                _tradedSymbol = front;
                _closes.Reset();
                _ranges.Reset();
            }

            if (bar.Symbol != _tradedSymbol) return;

            if (bar.EndTime.Date != _sessionDate)
            {
                _sessionDate = bar.EndTime.Date;
                _tradesToday = 0;
            }

            _closes.Add(bar.Close);
            _ranges.Add(bar.High - bar.Low);

            if (_inPosition)
            {
                ManagePosition(bar, t);
                return;
            }

            if (!_closes.IsReady || !_ranges.IsReady) return;
            if (t < _tradeFrom || t > _tradeUntil) return;
            if (_tradesToday >= _maxTradesPerDay) return;

            // --- the entry: go with the recent move, but only if it is a real move ---
            var move = _closes[0] - _closes[_momentumBars];
            var volatility = 0m;
            for (var i = 0; i < _ranges.Count; i++) volatility += _ranges[i];
            volatility /= Math.Max(_ranges.Count, 1);

            var threshold = Math.Max(_minMovePoints, _minMoveVolFactor * volatility * _momentumBars);
            if (Math.Abs(move) < threshold)
            {
                _signalsFlat++;      // this is the filter that replicated out of sample
                return;
            }

            Enter(bar, move > 0 ? 1 : -1, volatility);
        }

        private void Enter(TradeBar bar, int direction, decimal volatility)
        {
            if (_tradedSymbol == null || !Securities.ContainsKey(_tradedSymbol)) return;
            if (!Securities[_tradedSymbol].Exchange.ExchangeOpen) return;

            var ticket = MarketOrder(_tradedSymbol, direction * _contracts,
                tag: $"momentum {direction * _contracts:+0;-0}");
            if (ticket == null || ticket.Status == OrderStatus.Invalid) return;

            _direction = direction;
            _entryPrice = ticket.AverageFillPrice > 0 ? ticket.AverageFillPrice : bar.Close;
            _barsInTrade = 0;
            _inPosition = true;
            _tradesToday++;
            _signalsTaken++;

            _stopPrice = _stopAtr > 0
                ? RoundToTick(_entryPrice - direction * _stopAtr * volatility * 3m)
                : 0m;
        }

        private void ManagePosition(TradeBar bar, TimeSpan t)
        {
            _barsInTrade++;

            if (t >= _flattenAt)
            {
                Exit(bar, "session close");
                _exitsSession++;
                return;
            }

            if (_stopPrice > 0)
            {
                var hit = _direction > 0 ? bar.Low <= _stopPrice : bar.High >= _stopPrice;
                if (hit)
                {
                    Exit(bar, "stop");
                    _exitsStop++;
                    return;
                }
            }

            if (_barsInTrade >= _maxHoldBars)
            {
                Exit(bar, "time stop");
                _exitsTime++;
            }
        }

        private void Exit(TradeBar bar, string reason)
        {
            if (_tradedSymbol != null && Portfolio.ContainsKey(_tradedSymbol)
                && Portfolio[_tradedSymbol].Invested)
            {
                Liquidate(_tradedSymbol, tag: reason);
            }
            _inPosition = false;
        }

        private decimal RoundToTick(decimal price)
        {
            if (_tickSize <= 0) return price;
            return Math.Round(price / _tickSize, MidpointRounding.AwayFromZero) * _tickSize;
        }

        public override void OnEndOfAlgorithm()
        {
            var closed = TradeBuilder.ClosedTrades;
            var net = Portfolio.TotalPortfolioValue - _startingEquity;
            var years = (EndDate - StartDate).TotalDays / 365.25;
            var fees = closed.Sum(x => x.TotalFees);
            var wins = closed.Count(x => x.ProfitLoss > 0);

            Log("==================== ERGEBNIS ====================");
            Log($"Signale genommen   : {_signalsTaken}   " +
                $"wegen flachem Markt verworfen: {_signalsFlat}");
            Log($"Ausstiege          : Zeit {_exitsTime}, Stop {_exitsStop}, " +
                $"Sessionende {_exitsSession}");
            Log($"Geschlossene Trades: {closed.Count}" +
                (closed.Count > 0 ? $"   Trefferquote {100m * wins / closed.Count:F1}%" : ""));
            Log($"Gebuehren          : {fees:N0} CHF");
            Log($"NETTO              : {net:N0} CHF   " +
                $"({net / (decimal)Math.Max(years, 0.01):N0} pro Jahr)");
            if (closed.Count > 0)
            {
                Log($"pro Trade          : {net / closed.Count:N0} CHF " +
                    $"({net / closed.Count / _multiplier / _contracts:F2} Indexpunkte)");
            }
            Log("=================================================");
        }
    }
}
