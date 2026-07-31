using System;
using System.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;
using QuantConnect.Util;

namespace QuantConnect.Algorithm.CSharp
{
    public class SMIVWAPClaudeCode : QCAlgorithm
    {
        private Future _smiFuture;
        private Symbol _currentContract;

        private RollingWindow<TradeBar> _consolidatedBars;

        private ExponentialMovingAverage _emaFast;
        private ExponentialMovingAverage _emaSlow;
        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;

        private SimpleMovingAverage _avgVolume;
        private decimal _volumeMultiplier = 1.5m;

        private decimal _riskPerTrade = 0.02m;
        private int _maxDailyTrades = 5;
        private decimal _profitTargetMultiplier = 2.5m;
        private int _dailyTradeCount = 0;

        private int _fastEmaPeriod = 8;
        private int _slowEmaPeriod = 21;
        private int _atrPeriod = 14;
        private int _rsiPeriod = 14;
        private int _volumePeriod = 20;

        private DateTime _lastTradeDate;
        private bool _inPosition = false;
        private DateTime _lastSignalTime;

        public override void Initialize()
        {
            SetStartDate(2025, 1, 1);
            SetEndDate(2026, 1, 1);
            SetCash(100000);
            SetTimeZone(TimeZones.Zurich);

            _smiFuture = AddFuture(
                Futures.Indices.SMI,
                Resolution.Tick,
                dataNormalizationMode: DataNormalizationMode.BackwardsRatio,
                dataMappingMode: DataMappingMode.OpenInterest,
                contractDepthOffset: 0
            );

            _smiFuture.SetFilter(0, 90);

            _consolidatedBars = new RollingWindow<TradeBar>(50);

            SetWarmUp(TimeSpan.FromDays(5));

            Schedule.On(
                DateRules.EveryDay(_smiFuture.Symbol),
                TimeRules.AfterMarketOpen(_smiFuture.Symbol, 1),
                () => { _dailyTradeCount = 0; }
            );

            Schedule.On(
                DateRules.EveryDay(_smiFuture.Symbol),
                TimeRules.BeforeMarketClose(_smiFuture.Symbol, 15),
                FlattenAllPositions
            );
        }

        public override void OnData(Slice slice)
        {
            if (!slice.FuturesChains.ContainsKey(_smiFuture.Symbol))
                return;

            var chain = slice.FuturesChains[_smiFuture.Symbol];

            // Select front month contract with highest open interest
            var contract = chain
                .OrderByDescending(x => x.OpenInterest)
                .ThenBy(x => x.Expiry)
                .FirstOrDefault();

            if (contract == null)
                return;

            // Roll to new contract if needed
            if (_currentContract != contract.Symbol)
            {
                if (_currentContract != null && Portfolio[_currentContract].Invested)
                {
                    Liquidate(_currentContract, "Contract Roll");
                    _inPosition = false;
                }

                _currentContract = contract.Symbol;

                // Initialize indicators for new contract
                _emaFast = new ExponentialMovingAverage(_fastEmaPeriod);
                _emaSlow = new ExponentialMovingAverage(_slowEmaPeriod);
                _rsi = new RelativeStrengthIndex(_rsiPeriod);
                _atr = new AverageTrueRange(_atrPeriod);
                _avgVolume = new SimpleMovingAverage(_volumePeriod);

                // Create 5-minute consolidator
                var consolidator = new TradeBarConsolidator(TimeSpan.FromMinutes(5));
                consolidator.DataConsolidated += OnFiveMinuteBar;
                SubscriptionManager.AddConsolidator(_currentContract, consolidator);
            }
        }

        private void OnFiveMinuteBar(object sender, TradeBar bar)
        {
            if (IsWarmingUp)
                return;

            _consolidatedBars.Add(bar);

            // Update indicators
            _emaFast.Update(bar.EndTime, bar.Close);
            _emaSlow.Update(bar.EndTime, bar.Close);
            _rsi.Update(bar.EndTime, bar.Close);
            _atr.Update(bar);
            _avgVolume.Update(bar.EndTime, bar.Volume);

            if (!_consolidatedBars.IsReady || !_emaFast.IsReady || !_emaSlow.IsReady
                || !_rsi.IsReady || !_atr.IsReady || !_avgVolume.IsReady)
                return;

            if (_lastTradeDate.Date != Time.Date)
            {
                _dailyTradeCount = 0;
                _lastTradeDate = Time;
            }

            if (_dailyTradeCount >= _maxDailyTrades)
                return;

            if ((Time - _lastSignalTime).TotalMinutes < 5)
                return;

            var holding = Portfolio[_currentContract];
            var currentPrice = bar.Close;
            var currentVolume = bar.Volume;

            if (_inPosition && holding.Invested)
            {
                CheckExitConditions(bar);
                return;
            }

            if (!_inPosition && !holding.Invested)
            {
                CheckEntryConditions(bar, currentVolume);
            }
        }

        private void CheckEntryConditions(TradeBar bar, decimal currentVolume)
        {
            var currentPrice = bar.Close;
            var emaFastValue = _emaFast.Current.Value;
            var emaSlowValue = _emaSlow.Current.Value;
            var rsiValue = _rsi.Current.Value;

            var hasVolume = currentVolume > _avgVolume.Current.Value * _volumeMultiplier;

            if (!hasVolume)
                return;

            var emaDiff = Math.Abs(emaFastValue - emaSlowValue);
            var momentumStrength = emaDiff > _atr.Current.Value * 0.3m;

            if (!momentumStrength)
                return;

            // Long Setup
            if (emaFastValue > emaSlowValue && rsiValue > 40 && rsiValue < 70)
            {
                var barRange = bar.High - bar.Low;
                var closePosition = bar.Close - bar.Low;

                if (barRange > 0 && closePosition / barRange > 0.6m)
                {
                    EnterLongPosition(currentPrice);
                }
            }
            // Short Setup
            else if (emaFastValue < emaSlowValue && rsiValue < 60 && rsiValue > 30)
            {
                var barRange = bar.High - bar.Low;
                var closePosition = bar.High - bar.Close;

                if (barRange > 0 && closePosition / barRange > 0.6m)
                {
                    EnterShortPosition(currentPrice);
                }
            }
        }

        private void EnterLongPosition(decimal currentPrice)
        {
            var stopLoss = _atr.Current.Value * 2.0m;
            var contractSize = CalculatePositionSize(stopLoss);

            if (contractSize > 0)
            {
                MarketOrder(_currentContract, contractSize, tag: "Long");
                _inPosition = true;
                _dailyTradeCount++;
                _lastSignalTime = Time;

                var profitTarget = currentPrice + (stopLoss * _profitTargetMultiplier);
                var stopPrice = currentPrice - stopLoss;

                LimitOrder(_currentContract, -contractSize, profitTarget, tag: "TP");
                StopMarketOrder(_currentContract, -contractSize, stopPrice, tag: "SL");

                Debug($"LONG @ {currentPrice:F2} | SL: {stopPrice:F2} | TP: {profitTarget:F2}");
            }
        }

        private void EnterShortPosition(decimal currentPrice)
        {
            var stopLoss = _atr.Current.Value * 2.0m;
            var contractSize = CalculatePositionSize(stopLoss);

            if (contractSize > 0)
            {
                MarketOrder(_currentContract, -contractSize, tag: "Short");
                _inPosition = true;
                _dailyTradeCount++;
                _lastSignalTime = Time;

                var profitTarget = currentPrice - (stopLoss * _profitTargetMultiplier);
                var stopPrice = currentPrice + stopLoss;

                LimitOrder(_currentContract, contractSize, profitTarget, tag: "TP");
                StopMarketOrder(_currentContract, contractSize, stopPrice, tag: "SL");

                Debug($"SHORT @ {currentPrice:F2} | SL: {stopPrice:F2} | TP: {profitTarget:F2}");
            }
        }

        private void CheckExitConditions(TradeBar bar)
        {
            var holding = Portfolio[_currentContract];
            var wasLong = holding.IsLong;
            var wasShort = holding.IsShort;

            if (wasLong && _emaFast.Current.Value < _emaSlow.Current.Value && _rsi.Current.Value < 50)
            {
                Liquidate(_currentContract, "Reversal");
                CancelOpenOrders(_currentContract);
                _inPosition = false;
                Debug("Exit LONG - Reversal");
            }
            else if (wasShort && _emaFast.Current.Value > _emaSlow.Current.Value && _rsi.Current.Value > 50)
            {
                Liquidate(_currentContract, "Reversal");
                CancelOpenOrders(_currentContract);
                _inPosition = false;
                Debug("Exit SHORT - Reversal");
            }
        }

        private int CalculatePositionSize(decimal stopLossAmount)
        {
            var accountValue = Portfolio.TotalPortfolioValue;
            var riskAmount = accountValue * _riskPerTrade;
            var contractMultiplier = Securities[_currentContract].SymbolProperties.ContractMultiplier;
            var positionSize = (int)(riskAmount / (stopLossAmount * contractMultiplier));

            return Math.Max(1, positionSize);
        }

        private void FlattenAllPositions()
        {
            if (_currentContract != null && _inPosition && Portfolio[_currentContract].Invested)
            {
                Liquidate(_currentContract, "EOD");
                CancelOpenOrders(_currentContract);
                _inPosition = false;
            }
        }

        private void CancelOpenOrders(Symbol symbol)
        {
            foreach (var order in Transactions.GetOpenOrders(symbol))
            {
                Transactions.CancelOrder(order.Id);
            }
        }
    }
}
