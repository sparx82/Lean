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
using QuantConnect.Orders.Slippage;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// SMI long-term trend filter: hold the future while it trades above its 200-day average,
    /// stand aside below it. Roughly 7 position changes per year.
    ///
    /// WHY LOW FREQUENCY IS THE POINT
    /// Execution on this contract costs about 4.3 index points per round trip (measured
    /// against real fills, not assumed). At 100 trades a year that is 430 points, which kills
    /// any intraday edge. At 7 trades a year it is 30 points - noise. Cost sensitivity of this
    /// system, 1998-2025:
    ///
    ///   0 pts/round trip  +14'100/yr     8 pts  +12'956/yr
    ///   4.32 pts (real)   +13'482/yr    15 pts  +11'956/yr
    ///
    /// The strategy is essentially cost-immune. That is what makes it viable where everything
    /// faster was not.
    ///
    /// WHAT IT DOES AND DOES NOT DO (1998-2025, 6825 sessions, 3 contracts)
    ///
    ///   Buy and hold      +10'430/yr   max drawdown -168'210   Sharpe +0.25
    ///   SMA200 long/flat   +9'135/yr   max drawdown  -72'060   Sharpe +0.38
    ///
    /// At the SAME position size the trend filter does NOT beat the index on return. It earns
    /// slightly less while spending only 65% of the time in the market. What it does is cut
    /// the worst drawdown by 57%.
    ///
    /// HOW THAT BECOMES OUTPERFORMANCE
    /// With futures the drawdown saving converts into size. Risk-matched against buy and hold:
    ///
    ///   3 contracts   +9'135/yr    DD  -72'060
    ///   5 contracts  +15'225/yr    DD -120'100   still 29% less risk than B&H, +46% return
    ///   7 contracts  +21'315/yr    DD -168'140   exactly B&H's risk, roughly double return
    ///
    /// Beating the index is therefore a sizing decision, not a signal decision. Default here is
    /// 3 contracts, i.e. the conservative end; raising it requires the capital to survive the
    /// drawdown in the table above.
    ///
    /// ROBUSTNESS
    /// Lookback length is a plateau, not a lucky pick. Every length from 50 to 300 days cuts
    /// max drawdown to roughly -72k..-101k versus -168k for buy and hold. Sharpe by half of
    /// the sample, which is the check that matters:
    ///
    ///   length      1998-2011   2012-2025
    ///      150         +0.30       +0.26
    ///      200         +0.39       +0.40
    ///      250         +0.36       +0.34
    ///
    /// Buy and hold over the same halves: -0.08 then +0.55. The trend system's risk-adjusted
    /// profile is stable across regimes; the index's is not. Return ranking between lengths is
    /// NOT stable, so do not read the 200 as optimal - read 150-250 as one region.
    ///
    /// SEASONALITY IS DELIBERATELY OFF
    /// A Halloween overlay (long November-April) looked strong on the full sample (+11'059/yr
    /// at 49% exposure) but does not survive inspection:
    ///   - inside the window, January (-40'560) and February (-32'160) are the two worst
    ///     winter months, while October (+85'590), which is excluded, is one of the best
    ///   - moving the boundaries changes the result by a factor of 2.3 (Nov-Mar +6'693/yr,
    ///     Oct-May +15'412/yr) - that is a knife edge, not an effect
    ///   - in the 2012-2025 half, plain buy and hold (+23'112/yr) beats it (+15'654/yr)
    /// It is available via useSeasonalOverlay for inspection, but it is not recommended.
    ///
    /// HONEST LIMITATION
    /// This system beats buy and hold in 7 of 28 calendar years - and they are the bad ones
    /// (2001, 2002, 2008, 2011, 2022, 2023). It is drawdown insurance that pays for itself,
    /// not a return generator. In a long steady bull market it will lag, and it will whipsaw.
    /// </summary>
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="trend following" />
    /// <meta name="tag" content="strategy example" />
    public class FutureTrendAlgorithm_Oliver : QCAlgorithm
    {
        #region Configuration

        private string _root;
        private DateTime _startDate;
        private DateTime _endDate;

        private TimeSpan _sessionOpen;       // first minute the contract really trades
        private TimeSpan _sessionEnd;        // last minute it really trades
        private TimeSpan _sessionClose;      // reference close used for the signal
        private int _lookback;               // moving average length in sessions
        private int _contracts;
        private bool _allowShort;
        private bool _forceLong;
        private bool _useSeasonalOverlay;
        private int _rollDaysBeforeExpiry;
        private decimal _slippagePoints;

        #endregion

        #region State

        private Future _future;
        private Symbol _tradedSymbol;
        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators
            = new Dictionary<Symbol, IDataConsolidator>();

        private decimal _tickSize = 1m;
        private decimal _multiplier = 10m;

        // roll-adjusted price chain: only WITHIN-contract changes are accumulated, so the
        // carry gap at every roll never reaches the moving average
        private decimal _chainedPrice;
        private decimal _previousRawClose;
        private Symbol _previousContract;
        private bool _chainStarted;

        private SimpleMovingAverage _sma;
        private DateTime _sessionDate = DateTime.MinValue;
        private decimal _sessionClosePrice;

        private decimal _startingEquity;
        private DateTime _firstSessionDate = DateTime.MinValue;
        private int _targetPosition;
        private int _positionChanges;
        private int _sessionsProcessed;

        #endregion

        public override void Initialize()
        {
            SetAccountCurrency(GetParameter("accountCurrency", "CHF"));
            SetCash(GetParameter("startingCash", 200000));
            SetTimeZone("Europe/Zurich");
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);

            LoadParameters();

            SetStartDate(_startDate.Year, _startDate.Month, _startDate.Day);
            SetEndDate(_endDate.Year, _endDate.Month, _endDate.Day);

            // Minute is the coarsest resolution with local data for this contract; the daily
            // bar is assembled here rather than requested from the engine.
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

            _sma = new SimpleMovingAverage(_lookback);
            _startingEquity = Portfolio.TotalPortfolioValue;

            // Roughly _lookback trading sessions of calendar time, so a short backtest window
            // starts with a usable average instead of standing aside for its first ten months.
            SetWarmUp(TimeSpan.FromDays(_lookback * 1.5));

            SetBenchmark(_future.Symbol);

            Log($"Trend filter on {_root}: SMA{_lookback}, {_contracts} contract(s), " +
                $"short={_allowShort}, seasonal overlay={_useSeasonalOverlay}");
        }

        private void LoadParameters()
        {
            _root = GetParameter("root", Futures.Indices.SMI);
            _startDate = ParseDate(GetParameter("startDate", "19980720"), new DateTime(1998, 7, 20));
            _endDate = ParseDate(GetParameter("endDate", "20251003"), new DateTime(2025, 10, 3));

            // The contract trades 08:00-22:00. Lean's market hours database claims 01:10-22:00,
            // and with fill-forward on it fabricates minute bars from 01:10 that carry a stale
            // price. Without this gate the algorithm decides on the 17:30 close but executes on
            // a synthetic 01:11 bar, in a window where no market exists. Measured volume:
            // 08:00-09:00 2.5%, 09:00-17:30 92.5%, after 17:30 5.0%.
            _sessionOpen = ParseTime(GetParameter("sessionOpen", "08:00"));
            _sessionEnd = ParseTime(GetParameter("sessionEnd", "22:00"));
            _sessionClose = ParseTime(GetParameter("sessionClose", "17:30"));

            // Read 150-250 as one region; the 200 is not an optimum.
            _lookback = GetParameter("lookback", 200);
            _contracts = GetParameter("contracts", 3);

            // Long/short measured worse than long/flat (+7'655/yr, DD -106'620, Sharpe +0.22
            // versus +9'135, -72'060, +0.38), so shorting is off by default.
            _allowShort = GetParameter("allowShort", 0) == 1;
            _forceLong = GetParameter("forceLong", 0) == 1;

            // Off by default - see the class comment for why the seasonal edge does not hold up.
            _useSeasonalOverlay = GetParameter("useSeasonalOverlay", 0) == 1;

            _rollDaysBeforeExpiry = GetParameter("rollDaysBeforeExpiry", 3);
            // 0, NOT 1. Measured over 201 fills with the slippage model switched off, Lean
            // already fills buys above and sells below the traded price - an implied 3.93 point
            // round trip, i.e. the spread is inside the fill. Adding a slippage model on top
            // charges it twice.
            _slippagePoints = GetParameter("slippagePoints", 0m);
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
        /// Front contract = nearest expiry more than _rollDaysBeforeExpiry away. Future.Mapped
        /// is not used: it stays empty when the continuous mapping cannot be resolved locally,
        /// which silently freezes an algorithm on its first contract.
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

            // Ignore anything outside the hours the contract actually trades. This gate has to
            // come FIRST: rolling on a fabricated 01:11 bar liquidates the old contract and
            // buys the new one at two stale, mutually inconsistent prices, which costs real
            // money four times a year.
            if (timeOfDay < _sessionOpen || timeOfDay > _sessionEnd) return;

            var front = ResolveFrontContract(bar.EndTime);
            if (front == null) return;

            if (front != _tradedSymbol)
            {
                RollInto(front);
            }

            if (bar.Symbol != _tradedSymbol) return;

            // Running reference close for the signal.
            if (timeOfDay <= _sessionClose)
            {
                _sessionClosePrice = bar.Close;
            }

            // Decide and execute on the 17:30 bar itself: the 17:00-18:00 hour carries 24% of
            // the day's volume, so this is the most liquid moment available.
            if (timeOfDay >= _sessionClose && _sessionDate != bar.EndTime.Date)
            {
                _sessionDate = bar.EndTime.Date;
                CloseSession();
            }
        }

        /// <summary>
        /// Moves the position to the new front contract without changing net exposure.
        /// </summary>
        private void RollInto(Symbol front)
        {
            if (_tradedSymbol != null && Portfolio.ContainsKey(_tradedSymbol)
                && Portfolio[_tradedSymbol].Invested)
            {
                var quantity = Portfolio[_tradedSymbol].Quantity;
                Liquidate(_tradedSymbol, tag: "roll out");
                MarketOrder(front, quantity, tag: "roll in");
                Log($"{Time:yyyy-MM-dd} roll {_tradedSymbol.Value} -> {front.Value}, " +
                    $"carried {quantity} contract(s)");
            }

            _tradedSymbol = front;

            // The chain must not absorb the carry gap: restart it on the new contract.
            _previousContract = null;
        }

        /// <summary>
        /// Called once per session with the 17:30 close: extends the roll-adjusted chain,
        /// updates the moving average and sets the target position.
        /// </summary>
        private void CloseSession()
        {
            if (_sessionClosePrice <= 0) return;
            _sessionsProcessed++;
            if (_firstSessionDate == DateTime.MinValue && !IsWarmingUp)
            {
                _firstSessionDate = _sessionDate;
            }

            if (!_chainStarted)
            {
                _chainedPrice = _sessionClosePrice;
                _chainStarted = true;
            }
            else if (_previousContract == _tradedSymbol)
            {
                // same contract as yesterday -> a real price change
                _chainedPrice += _sessionClosePrice - _previousRawClose;
            }
            // different contract -> skip the gap, carry the chain level unchanged

            _previousRawClose = _sessionClosePrice;
            _previousContract = _tradedSymbol;

            _sma.Update(_sessionDate, _chainedPrice);
            if (!_sma.IsReady) return;

            // Feed the average during warm-up but take no decision: updating _targetPosition
            // here without being allowed to trade leaves the desired and the actual position
            // permanently out of sync, and the first position is then never opened.
            if (IsWarmingUp) return;

            // Diagnostic: forceLong=1 holds the position unconditionally, which turns this
            // algorithm into a buy-and-hold benchmark inside the same roll and P&L machinery.
            var above = _forceLong || _chainedPrice > _sma.Current.Value;
            var desired = above ? _contracts : (_allowShort ? -_contracts : 0);

            if (_useSeasonalOverlay && desired == 0)
            {
                // winter half of the year, the classic Halloween window
                var month = _sessionDate.Month;
                if (month >= 11 || month <= 4) desired = _contracts;
            }

            if (desired != _targetPosition)
            {
                _targetPosition = desired;
                Rebalance();
            }
        }

        private void Rebalance()
        {
            if (_tradedSymbol == null || !Securities.ContainsKey(_tradedSymbol)) return;

            var current = Portfolio.ContainsKey(_tradedSymbol)
                ? (int)Portfolio[_tradedSymbol].Quantity
                : 0;

            var delta = _targetPosition - current;
            if (delta == 0) return;

            MarketOrder(_tradedSymbol, delta,
                tag: $"trend -> {_targetPosition} contract(s), close {_chainedPrice:F0} " +
                     $"vs SMA{_lookback} {_sma.Current.Value:F0}");

            _positionChanges++;
        }

        public override void OnEndOfAlgorithm()
        {
            var closed = TradeBuilder.ClosedTrades;
            // Measured from the first session actually processed: with a start date earlier
            // than the available contract chain, EndDate - StartDate overstates the period and
            // silently deflates the per-year figure.
            var tradedFrom = _firstSessionDate == DateTime.MinValue ? StartDate : _firstSessionDate;
            var years = (EndDate - tradedFrom).TotalDays / 365.25;
            var net = Portfolio.TotalPortfolioValue - _startingEquity;

            Log("==================== SUMMARY ====================");
            Log($"Traded from        : {_firstSessionDate:yyyy-MM-dd} " +
                $"({_sessionsProcessed} sessions processed)");
            Log($"Position changes   : {_positionChanges} " +
                $"({_positionChanges / Math.Max(years, 0.01):F1} per year)");
            Log($"Closed trades      : {closed.Count}");
            Log($"Fees               : {closed.Sum(t => t.TotalFees):F0} CHF");
            Log($"Net P/L            : {net:F0} CHF  ({net / (decimal)Math.Max(years, 0.01):F0} per year)");
            Log("=================================================");
        }

        /// <summary>
        /// Fixed number of index points per side, matching the SMI's 2-point median spread.
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
