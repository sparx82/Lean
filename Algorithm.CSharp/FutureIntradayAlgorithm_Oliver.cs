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
    /// Intraday day-trading laboratory for index futures (default: EUREX SMI).
    ///
    /// The algorithm is one execution/risk engine with six interchangeable signal generators,
    /// selected via the "strategy" parameter:
    ///
    ///   vwap-reversion  Fade stretched moves away from the session VWAP, exit back at VWAP.
    ///   vwap-trend      Trade with the session trend, enter on pullbacks to VWAP.
    ///   orb             Opening range breakout, filtered by opening-range width vs ATR.
    ///   band-fade       Fade Bollinger band extremes confirmed by RSI, exit at the middle band.
    ///   ema-adx         EMA crossover, only in an ADX-confirmed trend regime.
    ///   combo           Regime switch: trend regime -> orb / ema-adx, range regime -> a
    ///                   confirmed vote of vwap-reversion + band-fade.
    ///
    /// Everything below the signal (position sizing, bracket exits, break-even, trailing,
    /// time stop, session flattening, daily loss/trade limits, rollover) is shared, so the
    /// signals can be compared on identical risk assumptions.
    ///
    /// COST NOTE: the SMI future has a median bid/ask spread of 2 index points (CHF 20 per
    /// contract round trip) plus commission. Measured over 1199 sessions (2021-2025), naive
    /// intraday directional signals on this contract produce edges below 1 point. Any
    /// configuration therefore has to hold for a meaningful move; high-frequency scalping of
    /// this contract is negative by construction. See _minEdgePoints below.
    ///
    /// MEASURED BASELINE, 2023-01-02 .. 2025-10-03, 5-minute bars, defaults below, expectancy
    /// in index points per trade (CHF 10 per point per contract):
    ///
    ///   combo           140 trades   -2.6 pts   (-1.7 pts with slippage switched off)
    ///   ema-adx         710 trades   -5.5 pts   (-4.4 pts with slippage switched off)
    ///   vwap-trend      311 trades   -6.0 pts
    ///   orb             479 trades   -6.4 pts
    ///   vwap-reversion  771 trades   -7.7 pts
    ///   band-fade         9 trades  -12.2 pts
    ///
    /// Every variant is negative, and still negative at zero transaction cost. Treat this file
    /// as the test bench that produced those numbers, not as a strategy that is ready to run.
    /// </summary>
    /// <meta name="tag" content="futures" />
    /// <meta name="tag" content="day trading" />
    /// <meta name="tag" content="indicators" />
    /// <meta name="tag" content="strategy example" />
    public class FutureIntradayAlgorithm_Oliver : QCAlgorithm
    {
        private enum Strategy
        {
            VwapReversion,
            VwapTrend,
            OpeningRangeBreakout,
            BandFade,
            EmaAdx,
            Combo
        }

        private enum TradeState
        {
            Flat,
            PendingEntry,
            InPosition
        }

        #region Configuration (all overridable via GetParameter / optimization)

        private Strategy _strategy;
        private string _root;               // contract root, e.g. SMI or FESX
        private DateTime _startDate;
        private DateTime _endDate;
        private int _barMinutes;            // signal bar size in minutes
        private int _warmUpDays;            // calendar days of data fed in before trading
        private int _rollDaysBeforeExpiry;  // switch to the next contract this early
        private bool _useTickData;          // subscribe to ticks instead of minute bars

        // --- session windows (exchange time, Europe/Zurich) ---
        private TimeSpan _sessionStart;     // start of the cash session, VWAP/OR anchor
        private TimeSpan _tradeFrom;        // no entries before this
        private TimeSpan _tradeUntil;       // no new entries after this
        private TimeSpan _flattenAt;        // force flat here, no overnight risk

        // --- risk ---
        private decimal _riskPerTradeChf;   // cash risked between entry and stop
        private int _maxContracts;
        private decimal _stopAtr;           // stop distance = _stopAtr * ATR
        private decimal _targetR;           // target = _targetR * risk
        private decimal _breakEvenR;        // move stop to entry after this much profit (0 = off)
        private decimal _trailAtr;          // trailing stop distance in ATR (0 = off)
        private int _maxBarsInTrade;        // time stop
        private decimal _minEdgePoints;     // reject setups whose target is smaller than this

        // --- daily guards ---
        private int _maxTradesPerDay;
        private decimal _dailyLossLimitChf;
        private decimal _dailyProfitLockChf; // stop trading once this is banked (0 = off)
        private int _cooldownBarsAfterLoss;
        private int _maxConsecutiveLosses;

        // --- signal parameters ---
        private int _atrPeriod;
        private int _adxPeriod;
        private int _rsiPeriod;
        private int _emaFastPeriod;
        private int _emaSlowPeriod;
        private int _bbPeriod;
        private decimal _bbDeviation;
        private decimal _entryDeviation;    // VWAP distance for a fade, in ATR (or sigma) units
        private decimal _pullbackDeviation; // VWAP distance for a trend pullback, in ATR units
        private bool _useSigmaScale;        // scale VWAP distance by stdev instead of ATR
        private decimal _adxTrendMin;       // ADX above this = trend regime
        private decimal _adxRangeMax;       // ADX below this = range regime
        private decimal _rsiOverbought;
        private decimal _rsiOversold;
        private int _openingRangeMinutes;
        private decimal _orMinAtr;          // opening range must be at least this many ATRs
        private decimal _orMaxAtr;          // ... and at most this many (wide ranges fail more)
        private TimeSpan _orbCutoff;        // no breakout entries after this time
        private int _erPeriod;              // Kaufman efficiency ratio lookback for the regime
        private decimal _erTrendMin;

        // --- execution ---
        private bool _useLimitEntry;        // post passively instead of paying the spread
        private int _entryOffsetTicks;      // improve the passive price by this many ticks
        private int _entryTimeoutBars;      // cancel an unfilled entry after this many bars
        private decimal _slippagePoints;    // modelled slippage per side, in index points
        private bool _enableCharts;

        #endregion

        #region State

        private Future _future;
        private Symbol _tradedSymbol;
        private readonly Dictionary<Symbol, IDataConsolidator> _consolidators = new Dictionary<Symbol, IDataConsolidator>();
        private decimal _tickSize = 1m;
        private decimal _multiplier = 10m;

        private AverageTrueRange _atr;
        private AverageDirectionalIndex _adx;
        private RelativeStrengthIndex _rsi;
        private ExponentialMovingAverage _emaFast;
        private ExponentialMovingAverage _emaSlow;
        private BollingerBands _bb;
        private StandardDeviation _vwapDeviationStd;
        private RollingWindow<decimal> _closes;

        // session
        private DateTime _sessionDate = DateTime.MinValue;
        private decimal _cumulativePriceVolume;
        private decimal _cumulativeVolume;
        private decimal _sessionVwap;
        private decimal _openingRangeHigh;
        private decimal _openingRangeLow;
        private bool _openingRangeReady;
        private bool _orbTradedLong;
        private bool _orbTradedShort;
        private decimal _prevClose;

        // daily guards
        private decimal _dayStartEquity;
        private int _tradesToday;
        private int _consecutiveLosses;
        private int _cooldownRemaining;
        private bool _haltedToday;

        // open position
        private TradeState _state = TradeState.Flat;
        private int _direction;
        private decimal _entryPrice;
        private decimal _stopPrice;
        private decimal _targetPrice;
        private decimal _riskPoints;
        private int _barsInTrade;
        private int _barsPending;
        private bool _breakEvenDone;
        private decimal _equityAtEntry;
        private string _entryReason = string.Empty;

        private OrderTicket _entryTicket;
        private OrderTicket _stopTicket;
        private OrderTicket _targetTicket;

        // statistics
        private int _wins;
        private int _losses;
        private int _signalsRejectedByEdge;
        private int _signalsRejectedByGuards;
        private int _barsProcessed;
        private int _signalBarsEvaluated;
        private decimal _grossPoints;

        #endregion

        public override void Initialize()
        {
            // Account currency has to be set before any cash is booked. Keep it equal to the
            // contract's quote currency (CHF for SMI, EUR for FESX) to avoid needing an FX
            // conversion series in the data folder.
            SetAccountCurrency(GetParameter("accountCurrency", "CHF"));
            SetCash(GetParameter("startingCash", 100000));
            SetTimeZone("Europe/Zurich");

            // Futures need a margin account - a cash account cannot hold a short future.
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);

            LoadParameters();

            SetStartDate(_startDate.Year, _startDate.Month, _startDate.Day);
            SetEndDate(_endDate.Year, _endDate.Month, _endDate.Day);

            var resolution = _useTickData ? Resolution.Tick : Resolution.Minute;

            // AddFuture gives us the contract universe and the auxiliary data. Signals and
            // orders both run on the individual front contract picked by ResolveFrontContract,
            // so every price level used for a stop or a limit is a real, tradable price.
            _future = AddFuture(
                _root,
                resolution,
                Market.EUREX,
                dataMappingMode: DataMappingMode.OpenInterest,
                dataNormalizationMode: DataNormalizationMode.BackwardsRatio,
                contractDepthOffset: 0);

            // At least two contracts have to be in the universe so the next one is already
            // subscribed when the front month approaches expiry.
            _future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(182));

            SetSecurityInitializer(security =>
            {
                if (security.Type != SecurityType.Future) return;

                security.SetFeeModel(new InteractiveBrokersFeeModel());
                // The spread, not the commission, is the dominant cost on this contract.
                security.SetSlippageModel(new FixedPointSlippageModel(_slippagePoints));
            });

            _tickSize = _future.SymbolProperties.MinimumPriceVariation;
            _multiplier = _future.SymbolProperties.ContractMultiplier;

            CreateIndicators();

            // Signal bars are built per contract in OnSecuritiesChanged. Data arrives under
            // the individual contract symbols, never under the canonical /SMI symbol, so a
            // consolidator registered on the canonical symbol would never fire.

            // Native warm-up feeds the subscription's own data type through the consolidator,
            // so it works for both the minute and the tick path.
            SetWarmUp(TimeSpan.FromDays(_warmUpDays));

            SetBenchmark(_future.Symbol);

            Log($"Strategy={_strategy} root={_root} bar={_barMinutes}min resolution={resolution} " +
                $"risk={_riskPerTradeChf} CHF stop={_stopAtr}xATR target={_targetR}R " +
                $"tick={_tickSize} multiplier={_multiplier}");
        }

        #region Setup

        private void LoadParameters()
        {
            _strategy = ParseStrategy(GetParameter("strategy", "vwap-reversion"));
            // Tick size, multiplier and currency are read from the symbol properties
            // database, so any root works - but only SMI has full local coverage
            // (1998-2025). FESX is present as a three-day sample only.
            _root = GetParameter("root", Futures.Indices.SMI);
            // Local SMI trade ticks end on 2025-10-03.
            _startDate = ParseDate(GetParameter("startDate", "20230102"), new DateTime(2023, 1, 2));
            _endDate = ParseDate(GetParameter("endDate", "20251003"), new DateTime(2025, 10, 3));
            _barMinutes = GetParameter("barMinutes", 5);
            _warmUpDays = GetParameter("warmUpDays", 5);
            _rollDaysBeforeExpiry = GetParameter("rollDaysBeforeExpiry", 3);
            _useTickData = GetParameter("useTickData", 0) == 1;

            _sessionStart = ParseTime(GetParameter("sessionStart", "09:00"));
            _tradeFrom = ParseTime(GetParameter("tradeFrom", "09:15"));
            _tradeUntil = ParseTime(GetParameter("tradeUntil", "16:45"));
            _flattenAt = ParseTime(GetParameter("flattenAt", "17:20"));

            _riskPerTradeChf = GetParameter("riskPerTradeChf", 300m);
            _maxContracts = GetParameter("maxContracts", 2);
            _stopAtr = GetParameter("stopAtr", 1.5m);
            _targetR = GetParameter("targetR", 2.0m);
            _breakEvenR = GetParameter("breakEvenR", 1.0m);
            _trailAtr = GetParameter("trailAtr", 2.0m);
            _maxBarsInTrade = GetParameter("maxBarsInTrade", 24);
            _minEdgePoints = GetParameter("minEdgePoints", 15m);

            _maxTradesPerDay = GetParameter("maxTradesPerDay", 3);
            _dailyLossLimitChf = GetParameter("dailyLossLimitChf", 900m);
            _dailyProfitLockChf = GetParameter("dailyProfitLockChf", 0m);
            _cooldownBarsAfterLoss = GetParameter("cooldownBarsAfterLoss", 3);
            _maxConsecutiveLosses = GetParameter("maxConsecutiveLosses", 3);

            _atrPeriod = GetParameter("atrPeriod", 14);
            _adxPeriod = GetParameter("adxPeriod", 14);
            _rsiPeriod = GetParameter("rsiPeriod", 14);
            _emaFastPeriod = GetParameter("emaFastPeriod", 9);
            _emaSlowPeriod = GetParameter("emaSlowPeriod", 21);
            _bbPeriod = GetParameter("bbPeriod", 20);
            _bbDeviation = GetParameter("bbDeviation", 2.0m);
            _entryDeviation = GetParameter("entryDeviation", 2.0m);
            _pullbackDeviation = GetParameter("pullbackDeviation", 0.3m);
            _useSigmaScale = GetParameter("useSigmaScale", 0) == 1;
            _adxTrendMin = GetParameter("adxTrendMin", 25m);
            _adxRangeMax = GetParameter("adxRangeMax", 20m);
            _rsiOverbought = GetParameter("rsiOverbought", 70m);
            _rsiOversold = GetParameter("rsiOversold", 30m);
            _openingRangeMinutes = GetParameter("openingRangeMinutes", 30);
            _orMinAtr = GetParameter("orMinAtr", 2.0m);
            _orMaxAtr = GetParameter("orMaxAtr", 6.0m);
            _orbCutoff = ParseTime(GetParameter("orbCutoff", "12:00"));
            _erPeriod = GetParameter("erPeriod", 20);
            _erTrendMin = GetParameter("erTrendMin", 0.35m);

            _useLimitEntry = GetParameter("useLimitEntry", 1) == 1;
            _entryOffsetTicks = GetParameter("entryOffsetTicks", 0);
            _entryTimeoutBars = GetParameter("entryTimeoutBars", 2);
            _slippagePoints = GetParameter("slippagePoints", 1.0m);
            _enableCharts = GetParameter("enableCharts", 0) == 1;
        }

        private static Strategy ParseStrategy(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "vwap-trend": return Strategy.VwapTrend;
                case "orb": return Strategy.OpeningRangeBreakout;
                case "band-fade": return Strategy.BandFade;
                case "ema-adx": return Strategy.EmaAdx;
                case "combo": return Strategy.Combo;
                default: return Strategy.VwapReversion;
            }
        }

        private static DateTime ParseDate(string value, DateTime fallback)
        {
            DateTime parsed;
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed)
                ? parsed
                : fallback;
        }

        private static TimeSpan ParseTime(string value)
        {
            TimeSpan parsed;
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : TimeSpan.Zero;
        }

        private void CreateIndicators()
        {
            _atr = new AverageTrueRange(_atrPeriod, MovingAverageType.Wilders);
            _adx = new AverageDirectionalIndex(_adxPeriod);
            _rsi = new RelativeStrengthIndex(_rsiPeriod, MovingAverageType.Wilders);
            _emaFast = new ExponentialMovingAverage(_emaFastPeriod);
            _emaSlow = new ExponentialMovingAverage(_emaSlowPeriod);
            _bb = new BollingerBands(_bbPeriod, _bbDeviation);
            _vwapDeviationStd = new StandardDeviation(_bbPeriod);
            _closes = new RollingWindow<decimal>(Math.Max(_erPeriod + 1, 3));
        }

        #endregion

        #region Data handling

        public override void OnData(Slice slice)
        {
            // Signal logic lives in OnBar; OnData would fire once per tick.
        }

        /// <summary>
        /// Attaches a signal-bar consolidator to every contract the filter puts in the
        /// universe and removes it again when the contract leaves.
        /// </summary>
        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                var symbol = security.Symbol;
                if (symbol.SecurityType != SecurityType.Future || symbol.IsCanonical()) continue;
                if (_consolidators.ContainsKey(symbol)) continue;

                IDataConsolidator consolidator;
                if (_useTickData)
                {
                    var tickConsolidator = new TickConsolidator(TimeSpan.FromMinutes(_barMinutes));
                    tickConsolidator.DataConsolidated += OnBar;
                    consolidator = tickConsolidator;
                }
                else
                {
                    var barConsolidator = new TradeBarConsolidator(TimeSpan.FromMinutes(_barMinutes));
                    barConsolidator.DataConsolidated += OnBar;
                    consolidator = barConsolidator;
                }

                SubscriptionManager.AddConsolidator(symbol, consolidator);
                _consolidators[symbol] = consolidator;
            }

            foreach (var security in changes.RemovedSecurities)
            {
                IDataConsolidator consolidator;
                if (!_consolidators.TryGetValue(security.Symbol, out consolidator)) continue;

                SubscriptionManager.RemoveConsolidator(security.Symbol, consolidator);

                var tickConsolidator = consolidator as TickConsolidator;
                if (tickConsolidator != null) tickConsolidator.DataConsolidated -= OnBar;

                var barConsolidator = consolidator as TradeBarConsolidator;
                if (barConsolidator != null) barConsolidator.DataConsolidated -= OnBar;

                _consolidators.Remove(security.Symbol);
            }
        }

        private void UpdateIndicators(TradeBar bar)
        {
            _atr.Update(bar);
            _adx.Update(bar);
            _rsi.Update(bar.EndTime, bar.Close);
            _emaFast.Update(bar.EndTime, bar.Close);
            _emaSlow.Update(bar.EndTime, bar.Close);
            _bb.Update(bar.EndTime, bar.Close);
            _closes.Add(bar.Close);
        }

        private void OnBar(object sender, TradeBar bar)
        {
            // Only the front contract drives the signals; back months are noise.
            var front = ResolveFrontContract(bar.EndTime);
            if (front == null) return;

            if (front != _tradedSymbol)
            {
                HandleRoll(front);
            }

            if (bar.Symbol != _tradedSymbol) return;

            _barsProcessed++;
            UpdateIndicators(bar);

            var time = bar.EndTime;
            ResetOnNewSession(time);

            var timeOfDay = time.TimeOfDay;
            if (timeOfDay < _sessionStart)
            {
                _prevClose = bar.Close;
                return;   // pre-session bars only feed the indicators
            }

            UpdateSessionState(bar);

            if (IsWarmingUp || !IndicatorsReady())
            {
                _prevClose = bar.Close;
                return;
            }

            if (_cooldownRemaining > 0) _cooldownRemaining--;

            ManageOpenPosition(bar);
            ManagePendingEntry(bar);

            if (_state == TradeState.Flat && CanOpenNewTrade(timeOfDay))
            {
                _signalBarsEvaluated++;
                var signal = EvaluateSignal(bar);
                if (signal != 0) TryEnter(bar, signal);
            }

            if (_enableCharts) PlotState(bar);

            _prevClose = bar.Close;
        }

        /// <summary>
        /// Picks the contract to trade: the nearest expiry that is still more than
        /// _rollDaysBeforeExpiry away.
        ///
        /// This deliberately does not use Future.Mapped. The continuous-contract mapping
        /// depends on auxiliary data (map files / open interest) that is not always resolved
        /// for every future in a local data folder - when it is not, Mapped stays empty and
        /// an algorithm keyed on it silently locks onto the first contract it ever saw and
        /// stops trading after that contract expires.
        /// </summary>
        private Symbol ResolveFrontContract(DateTime now)
        {
            Symbol front = null;

            foreach (var symbol in _consolidators.Keys)
            {
                var expiry = symbol.ID.Date;
                if ((expiry.Date - now.Date).TotalDays <= _rollDaysBeforeExpiry) continue;
                if (front == null || expiry < front.ID.Date) front = symbol;
            }

            return front;
        }

        /// <summary>
        /// Switches to a new front contract. Prices are per contract, so the indicator history
        /// from the old contract is not comparable and gets discarded.
        /// </summary>
        private void HandleRoll(Symbol front)
        {
            var previous = _tradedSymbol;

            if (previous != null)
            {
                Log($"{Time:yyyy-MM-dd HH:mm} roll {previous.Value} -> {front.Value}");

                CancelExitOrders();

                if (Portfolio.ContainsKey(previous) && Portfolio[previous].Invested)
                {
                    Liquidate(previous, tag: "rollover");
                }

                Transactions.CancelOpenOrders(previous);

                // Contract prices differ by the carry/dividend gap: restart the indicators.
                _atr.Reset();
                _adx.Reset();
                _rsi.Reset();
                _emaFast.Reset();
                _emaSlow.Reset();
                _bb.Reset();
                _closes.Reset();
                _prevClose = 0m;
            }

            _tradedSymbol = front;
            _state = TradeState.Flat;
            _entryTicket = null;
        }

        private void ResetOnNewSession(DateTime time)
        {
            if (time.Date == _sessionDate) return;

            _sessionDate = time.Date;
            _cumulativePriceVolume = 0m;
            _cumulativeVolume = 0m;
            _sessionVwap = 0m;
            _openingRangeHigh = 0m;
            _openingRangeLow = 0m;
            _openingRangeReady = false;
            _orbTradedLong = false;
            _orbTradedShort = false;
            _vwapDeviationStd.Reset();

            _dayStartEquity = Portfolio.TotalPortfolioValue;
            _tradesToday = 0;
            _haltedToday = false;
            _cooldownRemaining = 0;
            _consecutiveLosses = 0;   // the losing-streak pause is a daily circuit breaker
        }

        private void UpdateSessionState(TradeBar bar)
        {
            var typical = (bar.High + bar.Low + bar.Close) / 3m;
            var volume = bar.Volume > 0 ? bar.Volume : 1m;

            _cumulativePriceVolume += typical * volume;
            _cumulativeVolume += volume;
            _sessionVwap = _cumulativeVolume > 0 ? _cumulativePriceVolume / _cumulativeVolume : bar.Close;

            _vwapDeviationStd.Update(bar.EndTime, bar.Close - _sessionVwap);

            var openingRangeEnd = _sessionStart.Add(TimeSpan.FromMinutes(_openingRangeMinutes));
            if (bar.EndTime.TimeOfDay <= openingRangeEnd)
            {
                _openingRangeHigh = _openingRangeHigh == 0m ? bar.High : Math.Max(_openingRangeHigh, bar.High);
                _openingRangeLow = _openingRangeLow == 0m ? bar.Low : Math.Min(_openingRangeLow, bar.Low);
            }
            else if (_openingRangeHigh > 0m)
            {
                _openingRangeReady = true;
            }
        }

        private bool IndicatorsReady()
        {
            return _atr.IsReady && _atr > 0 && _adx.IsReady && _rsi.IsReady
                   && _emaFast.IsReady && _emaSlow.IsReady && _bb.IsReady && _closes.IsReady;
        }

        #endregion

        #region Signals

        /// <summary>
        /// Returns +1 for long, -1 for short, 0 for no trade, and records the reason.
        /// </summary>
        private int EvaluateSignal(TradeBar bar)
        {
            switch (_strategy)
            {
                case Strategy.VwapReversion: return VwapReversionSignal(bar);
                case Strategy.VwapTrend: return VwapTrendSignal(bar);
                case Strategy.OpeningRangeBreakout: return OpeningRangeSignal(bar);
                case Strategy.BandFade: return BandFadeSignal(bar);
                case Strategy.EmaAdx: return EmaAdxSignal(bar);
                case Strategy.Combo: return ComboSignal(bar);
                default: return 0;
            }
        }

        /// <summary>
        /// Distance from the session VWAP, expressed in ATR units (or in standard deviations
        /// of the VWAP distance when useSigmaScale is on).
        /// </summary>
        private decimal VwapDistance(decimal close)
        {
            var scale = _useSigmaScale && _vwapDeviationStd.IsReady && _vwapDeviationStd > 0
                ? _vwapDeviationStd.Current.Value
                : _atr.Current.Value;

            return scale > 0 ? (close - _sessionVwap) / scale : 0m;
        }

        /// <summary>
        /// Mean reversion: price is stretched away from VWAP in a low-ADX (range) regime and
        /// has stopped extending. Target is the VWAP itself.
        /// </summary>
        private int VwapReversionSignal(TradeBar bar)
        {
            if (_adx > _adxRangeMax) return 0;

            var distance = VwapDistance(bar.Close);

            // Require the last bar to push back against the stretch, so we are not
            // stepping in front of an accelerating move.
            if (distance >= _entryDeviation && bar.Close < _prevClose)
            {
                _entryReason = $"vwap-reversion short dist={distance:F2} adx={_adx.Current.Value:F1}";
                return -1;
            }

            if (distance <= -_entryDeviation && bar.Close > _prevClose)
            {
                _entryReason = $"vwap-reversion long dist={distance:F2} adx={_adx.Current.Value:F1}";
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Trend continuation: session trend defined by price vs VWAP and the EMA stack,
        /// entered on a pullback into the VWAP rather than on the breakout itself.
        /// </summary>
        private int VwapTrendSignal(TradeBar bar)
        {
            if (_adx < _adxTrendMin) return 0;

            var distance = VwapDistance(bar.Close);
            var upTrend = bar.Close > _sessionVwap && _emaFast > _emaSlow;
            var downTrend = bar.Close < _sessionVwap && _emaFast < _emaSlow;

            if (upTrend && distance <= _pullbackDeviation && bar.Close > bar.Open)
            {
                _entryReason = $"vwap-trend long pullback dist={distance:F2} adx={_adx.Current.Value:F1}";
                return 1;
            }

            if (downTrend && distance >= -_pullbackDeviation && bar.Close < bar.Open)
            {
                _entryReason = $"vwap-trend short pullback dist={distance:F2} adx={_adx.Current.Value:F1}";
                return -1;
            }

            return 0;
        }

        /// <summary>
        /// Opening range breakout, one attempt per side per day. The opening range width is
        /// filtered against ATR: very wide ranges historically give back the move, very narrow
        /// ranges break out into noise.
        /// </summary>
        private int OpeningRangeSignal(TradeBar bar)
        {
            if (!_openingRangeReady) return 0;
            if (bar.EndTime.TimeOfDay > _orbCutoff) return 0;

            var width = _openingRangeHigh - _openingRangeLow;
            var widthInAtr = _atr > 0 ? width / _atr.Current.Value : 0m;
            if (widthInAtr < _orMinAtr || widthInAtr > _orMaxAtr) return 0;

            if (!_orbTradedLong && bar.Close > _openingRangeHigh && _prevClose <= _openingRangeHigh)
            {
                _orbTradedLong = true;
                _entryReason = $"orb long break {_openingRangeHigh:F0} width={width:F0} ({widthInAtr:F1} ATR)";
                return 1;
            }

            if (!_orbTradedShort && bar.Close < _openingRangeLow && _prevClose >= _openingRangeLow)
            {
                _orbTradedShort = true;
                _entryReason = $"orb short break {_openingRangeLow:F0} width={width:F0} ({widthInAtr:F1} ATR)";
                return -1;
            }

            return 0;
        }

        /// <summary>
        /// Classic band fade: close outside the Bollinger band with RSI confirming the
        /// exhaustion, in a range regime. Exit is the middle band.
        /// </summary>
        private int BandFadeSignal(TradeBar bar)
        {
            if (_adx > _adxRangeMax) return 0;

            if (bar.Close > _bb.UpperBand && _rsi > _rsiOverbought && bar.Close < _prevClose)
            {
                _entryReason = $"band-fade short rsi={_rsi.Current.Value:F0}";
                return -1;
            }

            if (bar.Close < _bb.LowerBand && _rsi < _rsiOversold && bar.Close > _prevClose)
            {
                _entryReason = $"band-fade long rsi={_rsi.Current.Value:F0}";
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Momentum: EMA crossover taken only when ADX confirms a trend and price agrees with
        /// the session VWAP, which filters out most of the whipsaw crossovers.
        /// </summary>
        private int EmaAdxSignal(TradeBar bar)
        {
            if (_adx < _adxTrendMin) return 0;

            var crossedUp = _emaFast > _emaSlow && _prevClose <= _emaSlow;
            var crossedDown = _emaFast < _emaSlow && _prevClose >= _emaSlow;

            if (crossedUp && bar.Close > _sessionVwap)
            {
                _entryReason = $"ema-adx long adx={_adx.Current.Value:F1}";
                return 1;
            }

            if (crossedDown && bar.Close < _sessionVwap)
            {
                _entryReason = $"ema-adx short adx={_adx.Current.Value:F1}";
                return -1;
            }

            return 0;
        }

        /// <summary>
        /// Regime switch plus confirmation vote. Trend regime (ADX and Kaufman efficiency
        /// ratio both elevated) runs the breakout/momentum signals; range regime requires the
        /// two independent mean-reversion signals to agree before taking a trade.
        /// </summary>
        private int ComboSignal(TradeBar bar)
        {
            var efficiency = EfficiencyRatio();
            var trending = _adx >= _adxTrendMin && efficiency >= _erTrendMin;
            var ranging = _adx <= _adxRangeMax && efficiency < _erTrendMin;

            if (trending)
            {
                var orb = OpeningRangeSignal(bar);
                if (orb != 0)
                {
                    _entryReason = $"combo/trend {_entryReason} er={efficiency:F2}";
                    return orb;
                }

                var ema = EmaAdxSignal(bar);
                if (ema != 0)
                {
                    _entryReason = $"combo/trend {_entryReason} er={efficiency:F2}";
                    return ema;
                }

                return 0;
            }

            if (ranging)
            {
                var vote = VwapReversionSignal(bar) + BandFadeSignal(bar);
                if (vote >= 2)
                {
                    _entryReason = $"combo/range long vote=2 er={efficiency:F2}";
                    return 1;
                }

                if (vote <= -2)
                {
                    _entryReason = $"combo/range short vote=2 er={efficiency:F2}";
                    return -1;
                }
            }

            return 0;
        }

        /// <summary>
        /// Kaufman efficiency ratio: net move over the lookback divided by the path length.
        /// Near 1 means a clean trend, near 0 means chop.
        /// </summary>
        private decimal EfficiencyRatio()
        {
            if (!_closes.IsReady) return 0m;

            var lookback = Math.Min(_erPeriod, _closes.Count - 1);
            var net = Math.Abs(_closes[0] - _closes[lookback]);

            var path = 0m;
            for (var i = 0; i < lookback; i++)
            {
                path += Math.Abs(_closes[i] - _closes[i + 1]);
            }

            return path > 0 ? net / path : 0m;
        }

        #endregion

        #region Entry / exit management

        private bool CanOpenNewTrade(TimeSpan timeOfDay)
        {
            if (timeOfDay < _tradeFrom || timeOfDay > _tradeUntil) return false;
            if (_haltedToday || _cooldownRemaining > 0) return false;

            if (_tradesToday >= _maxTradesPerDay)
            {
                _signalsRejectedByGuards++;
                return false;
            }

            var dayPnl = Portfolio.TotalPortfolioValue - _dayStartEquity;

            if (dayPnl <= -_dailyLossLimitChf)
            {
                Log($"{Time:yyyy-MM-dd} daily loss limit hit ({dayPnl:F0} CHF) - flat for the day");
                _haltedToday = true;
                return false;
            }

            if (_dailyProfitLockChf > 0 && dayPnl >= _dailyProfitLockChf)
            {
                Log($"{Time:yyyy-MM-dd} daily profit locked ({dayPnl:F0} CHF) - flat for the day");
                _haltedToday = true;
                return false;
            }

            if (_maxConsecutiveLosses > 0 && _consecutiveLosses >= _maxConsecutiveLosses)
            {
                _haltedToday = true;
                return false;
            }

            return true;
        }

        private void TryEnter(TradeBar bar, int direction)
        {
            if (_tradedSymbol == null || !Securities.ContainsKey(_tradedSymbol)) return;
            if (!Securities[_tradedSymbol].Exchange.ExchangeOpen) return;

            var stopDistance = RoundToTick(_stopAtr * _atr.Current.Value);
            if (stopDistance < _tickSize) return;

            var reference = bar.Close;
            var stop = RoundToTick(reference - direction * stopDistance);
            var target = TargetFor(direction, reference, stopDistance);

            // Skip setups whose profit potential does not clear the round-trip cost by a
            // sensible margin. On the SMI this is the single most important filter.
            var edge = Math.Abs(target - reference);
            if (edge < _minEdgePoints)
            {
                _signalsRejectedByEdge++;
                return;
            }

            var quantity = PositionSize(stopDistance);
            if (quantity <= 0) return;

            _direction = direction;
            _riskPoints = stopDistance;
            _stopPrice = stop;
            _targetPrice = target;
            _entryPrice = reference;
            _barsInTrade = 0;
            _barsPending = 0;
            _breakEvenDone = false;
            _equityAtEntry = Portfolio.TotalPortfolioValue;

            var signedQuantity = direction * quantity;

            if (_useLimitEntry)
            {
                // Post on the passive side: buy at or below the close, sell at or above it.
                var limitPrice = RoundToTick(reference - direction * _entryOffsetTicks * _tickSize);
                _entryTicket = LimitOrder(_tradedSymbol, signedQuantity, limitPrice, tag: _entryReason);
            }
            else
            {
                _entryTicket = MarketOrder(_tradedSymbol, signedQuantity, tag: _entryReason);
            }

            _state = TradeState.PendingEntry;
        }

        private decimal TargetFor(int direction, decimal reference, decimal stopDistance)
        {
            decimal target;

            switch (_strategy)
            {
                case Strategy.VwapReversion:
                    target = _sessionVwap;   // mean reversion aims at the mean
                    break;
                case Strategy.BandFade:
                    target = _bb.MiddleBand.Current.Value;
                    break;
                default:
                    target = reference + direction * _targetR * stopDistance;
                    break;
            }

            // Never accept a target on the wrong side of the entry.
            if (direction > 0 && target <= reference) target = reference + _targetR * stopDistance;
            if (direction < 0 && target >= reference) target = reference - _targetR * stopDistance;

            return RoundToTick(target);
        }

        private int PositionSize(decimal stopDistance)
        {
            var riskPerContract = stopDistance * _multiplier;
            if (riskPerContract <= 0) return 0;

            var contracts = (int)Math.Floor(_riskPerTradeChf / riskPerContract);
            return Math.Max(0, Math.Min(contracts, _maxContracts));
        }

        private void ManagePendingEntry(TradeBar bar)
        {
            if (_state != TradeState.PendingEntry) return;

            // A cancelled, rejected or otherwise dead entry ticket must release the state
            // machine, otherwise the algorithm waits for a fill that can never arrive.
            if (_entryTicket == null || !_entryTicket.Status.IsOpen())
            {
                _state = TradeState.Flat;
                _entryTicket = null;
                return;
            }

            _barsPending++;
            if (_barsPending < _entryTimeoutBars) return;

            _entryTicket.Cancel("entry not filled in time");
            _state = TradeState.Flat;
            _entryTicket = null;
        }

        private void ManageOpenPosition(TradeBar bar)
        {
            if (_state != TradeState.InPosition) return;

            // Something outside this state machine flattened us (delisting liquidation,
            // rollover, manual intervention): drop back to Flat instead of managing a ghost.
            if (_tradedSymbol == null || !Portfolio[_tradedSymbol].Invested)
            {
                CancelExitOrders();
                _state = TradeState.Flat;
                return;
            }

            // Both bracket legs gone while still holding: re-arm, never sit unprotected.
            var stopAlive = _stopTicket != null && _stopTicket.Status.IsOpen();
            var targetAlive = _targetTicket != null && _targetTicket.Status.IsOpen();
            if (!stopAlive && !targetAlive)
            {
                PlaceBracket(Portfolio[_tradedSymbol].Quantity);
            }

            _barsInTrade++;

            var timeOfDay = bar.EndTime.TimeOfDay;
            if (timeOfDay >= _flattenAt)
            {
                ClosePosition("session end");
                return;
            }

            if (_maxBarsInTrade > 0 && _barsInTrade >= _maxBarsInTrade)
            {
                ClosePosition("time stop");
                return;
            }

            var openProfit = (bar.Close - _entryPrice) * _direction;

            // Break-even: once the trade has paid for itself, remove the initial risk.
            if (!_breakEvenDone && _breakEvenR > 0 && openProfit >= _breakEvenR * _riskPoints)
            {
                // Park the stop one tick beyond entry so the exit also covers the spread.
                var breakEven = RoundToTick(_entryPrice + _direction * _tickSize);
                MoveStop(breakEven, "break-even");
                _breakEvenDone = true;
            }

            // ATR trailing stop, only ever tightened.
            if (_trailAtr > 0 && _breakEvenDone)
            {
                var trail = RoundToTick(bar.Close - _direction * _trailAtr * _atr.Current.Value);
                if ((_direction > 0 && trail > _stopPrice) || (_direction < 0 && trail < _stopPrice))
                {
                    MoveStop(trail, "trail");
                }
            }
        }

        private void MoveStop(decimal newStop, string reason)
        {
            if (_stopTicket == null || !_stopTicket.Status.IsOpen()) return;

            var price = Securities[_tradedSymbol].Price;
            if (_direction > 0 && newStop >= price) return;   // would trigger immediately
            if (_direction < 0 && newStop <= price) return;

            _stopTicket.UpdateStopPrice(newStop, reason);
            _stopPrice = newStop;
        }

        private void ClosePosition(string reason)
        {
            CancelExitOrders();

            if (_tradedSymbol != null && Portfolio[_tradedSymbol].Invested)
            {
                Liquidate(_tradedSymbol, tag: reason);
            }

            _state = TradeState.Flat;
        }

        private void CancelExitOrders()
        {
            if (_stopTicket != null && _stopTicket.Status.IsOpen()) _stopTicket.Cancel();
            if (_targetTicket != null && _targetTicket.Status.IsOpen()) _targetTicket.Cancel();
            _stopTicket = null;
            _targetTicket = null;
        }

        private void PlaceBracket(decimal filledQuantity)
        {
            var exitQuantity = -filledQuantity;

            _stopTicket = StopMarketOrder(_tradedSymbol, exitQuantity, _stopPrice, tag: "protective stop");
            _targetTicket = LimitOrder(_tradedSymbol, exitQuantity, _targetPrice, tag: "profit target");
        }

        private decimal RoundToTick(decimal price)
        {
            if (_tickSize <= 0) return price;
            return Math.Round(price / _tickSize, MidpointRounding.AwayFromZero) * _tickSize;
        }

        #endregion

        #region Order and rollover events

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (_entryTicket != null && orderEvent.OrderId == _entryTicket.OrderId
                && (orderEvent.Status == OrderStatus.Canceled || orderEvent.Status == OrderStatus.Invalid))
            {
                if (_state == TradeState.PendingEntry)
                {
                    _state = TradeState.Flat;
                    _entryTicket = null;
                }
                return;
            }

            if (orderEvent.Status != OrderStatus.Filled && orderEvent.Status != OrderStatus.PartiallyFilled)
            {
                return;
            }

            // Entry filled -> attach the bracket.
            if (_entryTicket != null && orderEvent.OrderId == _entryTicket.OrderId
                && orderEvent.Status == OrderStatus.Filled)
            {
                _entryPrice = orderEvent.FillPrice;
                _state = TradeState.InPosition;
                _barsInTrade = 0;

                // Re-anchor the exits on the real fill price.
                _stopPrice = RoundToTick(_entryPrice - _direction * _riskPoints);
                _targetPrice = TargetFor(_direction, _entryPrice, _riskPoints);

                PlaceBracket(_entryTicket.QuantityFilled);
                _tradesToday++;

                Log($"{orderEvent.UtcTime:yyyy-MM-dd HH:mm} ENTRY {(_direction > 0 ? "LONG" : "SHORT")} " +
                    $"{_entryTicket.QuantityFilled} @ {_entryPrice:F0} stop={_stopPrice:F0} " +
                    $"target={_targetPrice:F0} | {_entryReason}");
                return;
            }

            // One leg of the bracket filled -> cancel the other (OCO) and book the result.
            var stopFilled = _stopTicket != null && orderEvent.OrderId == _stopTicket.OrderId;
            var targetFilled = _targetTicket != null && orderEvent.OrderId == _targetTicket.OrderId;

            if (stopFilled || targetFilled)
            {
                CancelExitOrders();
                BookTrade(orderEvent.FillPrice, stopFilled ? "stop" : "target");
            }
        }

        private void BookTrade(decimal exitPrice, string reason)
        {
            var points = (exitPrice - _entryPrice) * _direction;
            var pnl = Portfolio.TotalPortfolioValue - _equityAtEntry;

            _grossPoints += points;

            if (pnl >= 0)
            {
                _wins++;
                _consecutiveLosses = 0;
            }
            else
            {
                _losses++;
                _consecutiveLosses++;
                _cooldownRemaining = _cooldownBarsAfterLoss;
            }

            _state = TradeState.Flat;
            _entryTicket = null;

            Log($"{Time:yyyy-MM-dd HH:mm} EXIT {reason} @ {exitPrice:F0} " +
                $"points={points:F0} pnl={pnl:F0} CHF");
        }

        /// <summary>
        /// Belt and braces: if the engine does emit a mapping event, log it. The actual roll is
        /// driven by <see cref="ResolveFrontContract"/>, which does not depend on the mapping.
        /// </summary>
        public override void OnSymbolChangedEvents(SymbolChangedEvents symbolChangedEvents)
        {
            foreach (var symbol in symbolChangedEvents.Keys)
            {
                var changedEvent = symbolChangedEvents[symbol];
                Log($"Mapping event on {symbol}: {changedEvent.OldSymbol} -> {changedEvent.NewSymbol}");
            }
        }

        #endregion

        #region Reporting

        private void PlotState(TradeBar bar)
        {
            Plot("Price", "Close", bar.Close);
            Plot("Price", "VWAP", _sessionVwap);
            Plot("Regime", "ADX", _adx.Current.Value);
            Plot("Regime", "ATR", _atr.Current.Value);
        }

        public override void OnEndOfAlgorithm()
        {
            var closed = TradeBuilder.ClosedTrades;
            var trades = closed.Count;

            Log($"Bars processed: {_barsProcessed}, signal bars evaluated: {_signalBarsEvaluated}");

            if (trades == 0)
            {
                Log($"No trades. Signals rejected by edge filter: {_signalsRejectedByEdge}, " +
                    $"by daily guards: {_signalsRejectedByGuards}");
                return;
            }

            var grossProfit = closed.Sum(t => t.ProfitLoss);
            var fees = closed.Sum(t => t.TotalFees);
            var winners = closed.Where(t => t.ProfitLoss > 0).ToList();
            var losers = closed.Where(t => t.ProfitLoss <= 0).ToList();

            var avgWin = winners.Count > 0 ? winners.Average(t => t.ProfitLoss) : 0m;
            var avgLoss = losers.Count > 0 ? losers.Average(t => t.ProfitLoss) : 0m;
            var expectancy = (grossProfit - fees) / trades;

            Log("==================== SUMMARY ====================");
            Log($"Strategy            : {_strategy} ({_barMinutes}min bars)");
            Log($"Trades              : {trades}  (win {winners.Count} / loss {losers.Count}, " +
                $"hit rate {(decimal)winners.Count / trades * 100:F1}%)");
            Log($"Gross P/L           : {grossProfit:F0} CHF");
            Log($"Fees                : {fees:F0} CHF  ({fees / trades:F2} per trade)");
            Log($"Net P/L             : {grossProfit - fees:F0} CHF");
            Log($"Expectancy / trade  : {expectancy:F2} CHF  ({expectancy / _multiplier:F2} index points)");
            Log($"Average win / loss  : {avgWin:F0} / {avgLoss:F0} CHF");
            Log($"Gross index points  : {_grossPoints:F0}");
            Log($"Internal win/loss   : {_wins}/{_losses}");
            Log($"Rejected (edge)     : {_signalsRejectedByEdge}");
            Log($"Rejected (guards)   : {_signalsRejectedByGuards}");
            Log("=================================================");
        }

        #endregion

        /// <summary>
        /// Charges a fixed number of index points per side. The SMI future trades at a median
        /// spread of 2 points, so crossing it costs roughly 1 point per side - far more than
        /// the commission, and the reason most short-horizon signals on this contract lose.
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
