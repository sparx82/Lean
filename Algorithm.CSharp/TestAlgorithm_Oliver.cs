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

using Accord.Math;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Indicators;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Scheduling;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp
{
    public class TestAlgorithm_Oliver : QCAlgorithm
    {
        private List<IStrategy> _strategies;
        private IStrategy _activeStrategy;

        // Parameters for strategy selection and trading
        private TimeSpan SelectionInterval = TimeSpan.FromDays(20); // Re-evaluate strategy every 20 trading days
        private DateTime _nextSelectionTime;

        // We retain the current Sharpe to implement switching thresholds
        private double _currentStrategySharpe = -10.0;

        private Future future;
        private Symbol _activeSymbol;

        public override void Initialize()
        {
            SetTimeZone("Europe/Zurich");
            SetAccountCurrency("CHF");
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);

            SetStartDate(2024, 1, 1);
            SetEndDate(2024, 12, 31);
            SetCash(100000);

            // CHANGED: Use Tick resolution for source data
            future = AddFuture(Futures.Indices.SMI, Resolution.Tick, dataMappingMode: DataMappingMode.LastTradingDay, dataNormalizationMode: DataNormalizationMode.BackwardsRatio);
            future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(90));

            SetSecurityInitializer(security =>
            {
                if (security.Type == SecurityType.Future)
                {
                    security.SetFeeModel(new InteractiveBrokersFeeModel());
                }
            });

            _activeSymbol = future.Mapped;

            // Warm-up period to gather statistics for Sharpe calculation
            SetWarmUp(TimeSpan.FromDays(60));
            _nextSelectionTime = this.Time.Date.Add(SelectionInterval);

            // Initialize Strategies
            _strategies = new List<IStrategy>
            {
                new MovingAverageCrossover(this, future),
                new MeanReversion(this, future),
                new OpeningRangeBreakout(this, future)
            };

            // 1. Rebalancing Schedule
            Schedule.On(DateRules.EveryDay(future.Symbol), TimeRules.AfterMarketOpen(future.Symbol, 0), () =>
            {
                if (IsWarmingUp) return;

                // FIX: Ensure active strategy is initialized before checking rebalance
                if (_activeStrategy == null) return;

                if (this.Time.Date >= _nextSelectionTime.Date)
                {
                    RebalanceStrategy();
                    _nextSelectionTime = this.Time.Date.Add(SelectionInterval);
                }
            });

            // 2. End-Of-Day Statistics Schedule
            // We must trigger this for ALL strategies to calculate daily returns for Sharpe Ratios
            Schedule.On(DateRules.EveryDay(future.Symbol), TimeRules.BeforeMarketClose(future.Symbol, 1), () =>
            {
                foreach (var strategy in _strategies)
                {
                    strategy.OnEndOfDay();
                }

                // Enforce Day Trading Rule: Flatten active position
                if (_activeSymbol != null)
                {
                    Liquidate(_activeSymbol);
                    Debug("EOD: Positions Liquidated.");
                }
            });

            Debug("Algorithm Initialized. Warming up strategies with Sharpe Ratio tracking...");
        }

        // CHANGED: Add consolidators for new Future contracts automatically
        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                if (security.Type == SecurityType.Future)
                {
                    // Create a Tick Consolidator to generate 1-minute bars from ticks
                    var consolidator = new TickConsolidator(TimeSpan.FromMinutes(1));
                    consolidator.DataConsolidated += OnDataConsolidated;
                    SubscriptionManager.AddConsolidator(security.Symbol, consolidator);
                }
            }

            foreach (var security in changes.RemovedSecurities)
            {
                if (security.Type == SecurityType.Future)
                {
                    // Clean up consolidators to prevent memory leaks or duplicate events
                    SubscriptionManager.RemoveConsolidator(security.Symbol, (IDataConsolidator)null); // Removes all
                }
            }
        }

        // CHANGED: New handler for consolidated minute bars
        private void OnDataConsolidated(object sender, TradeBar bar)
        {
            // 1. Warm-up Phase
            if (IsWarmingUp)
            {
                foreach (var strategy in _strategies)
                {
                    strategy.Update(bar);
                }
                return;
            }

            // 2. Initial Selection
            if (_activeStrategy == null)
            {
                SelectBestStrategy();
                return;
            }

            // 3. Trading Phase
            // Update all strategies with the completed bar
            foreach (var strategy in _strategies)
            {
                strategy.Update(bar);
            }

            // 4. Execution
            // Ensure we execute on the active symbol corresponding to the data
            if (_activeSymbol != null)
            {
                _activeStrategy.ExecuteRealOrders(_activeSymbol);
            }
        }

        public override void OnData(Slice slice)
        {
            // CHANGED: Removed strategy updates from OnData. 
            // OnData now only handles Rollover Logic because it receives Ticks.

            // ROLLOVER LOGIC
            if (slice.SymbolChangedEvents.ContainsKey(future.Symbol))
            {
                _activeSymbol = slice.SymbolChangedEvents[future.Symbol].NewSymbol;
                Debug($"Rollover: Active Symbol changed to {_activeSymbol}");
            }
            else if (_activeSymbol == null || _activeSymbol == future.Symbol)
            {
                _activeSymbol = future.Mapped;
            }
        }

        private void SelectBestStrategy()
        {
            // Real-World: Select based on Risk-Adjusted Return (Sharpe), then Total Profit
            _activeStrategy = _strategies
                .OrderByDescending(s => s.GetSharpeRatio())
                .ThenByDescending(s => s.GetVirtualProfit())
                .FirstOrDefault();

            if (_activeStrategy != null)
            {
                _currentStrategySharpe = _activeStrategy.GetSharpeRatio();
                Debug($"Selected: {_activeStrategy.Name}. Sharpe: {_currentStrategySharpe:N2} | Virtual Profit: {_activeStrategy.GetVirtualProfit():N2}");

                // Reset the rebalance timer so we don't immediately rebalance after initial selection
                _nextSelectionTime = this.Time.Date.Add(SelectionInterval);
            }
            else
            {
                Error("No trading strategy could be selected.");
                _activeStrategy = _strategies.First();
            }
        }

        private void RebalanceStrategy()
        {
            // Find the best alternative
            var bestAlternative = _strategies
                .Where(s => s != _activeStrategy)
                .OrderByDescending(s => s.GetSharpeRatio())
                .FirstOrDefault();

            if (bestAlternative != null)
            {
                double currentSharpe = _activeStrategy.GetSharpeRatio();
                double altSharpe = bestAlternative.GetSharpeRatio();

                Debug($"Review: Active={_activeStrategy.Name}({currentSharpe:N2}) vs BestAlt={bestAlternative.Name}({altSharpe:N2})");

                // Switching Threshold: Only switch if the new strategy is significantly better
                // (e.g., Sharpe is +0.5 higher) to avoid "whipsawing" between strategies.
                if (altSharpe > (currentSharpe + 0.5))
                {
                    Liquidate(_activeSymbol); // Clean slate
                    _activeStrategy = bestAlternative;
                    _currentStrategySharpe = altSharpe;
                    Debug($"Strategy SWITCH: Now trading {_activeStrategy.Name}");
                }
            }
        }
    }
}

// ------------------------------------------------------------------------------------------------------
// Strategy Architecture
// ------------------------------------------------------------------------------------------------------

public interface IStrategy
{
    string Name { get; }
    // CHANGED: Update now accepts TradeBar directly
    void Update(TradeBar bar);
    void ExecuteRealOrders(Symbol activeSymbol);
    void OnEndOfDay();

    // Metrics
    double GetSharpeRatio();
    decimal GetVirtualProfit();
}

/// <summary>
/// Base class containing Real-World PnL and Sharpe Ratio logic for Shadow Tracking.
/// </summary>
public abstract class BaseStrategy : IStrategy
{
    public abstract string Name { get; }
    protected QCAlgorithm Algo;
    protected Symbol Symbol;

    // Virtual Tracking
    protected int VirtualPosition = 0;
    protected decimal VirtualPnL = 0m;
    protected decimal DailyVirtualPnL = 0m;
    protected List<double> DailyReturnsPct = new List<double>();

    // Config
    protected decimal AssumedCapital = 100000m;
    protected decimal TransactionCostPerTrade = 2.50m; // Approximate commission + slippage

    public BaseStrategy(QCAlgorithm algo, Future future)
    {
        Algo = algo;
        Symbol = future.Symbol;
    }

    /// <summary>
    /// Calculates signals and updates Virtual Position/PnL.
    /// MUST be called by the concrete implementation.
    /// </summary>
    // CHANGED: Signature update to accept TradeBar
    public abstract void Update(TradeBar bar);

    /// <summary>
    /// Executes physical orders if this strategy is active.
    /// </summary>
    public abstract void ExecuteRealOrders(Symbol activeSymbol);

    /// <summary>
    /// Updates the Virtual PnL based on the price movement and current virtual position.
    /// </summary>
    protected void TrackVirtualPerformance(TradeBar bar)
    {
        if (VirtualPosition != 0)
        {
            // Calculate point change
            decimal priceChange = bar.Close - bar.Open;

            // Basic PnL approximation: Points * Position * Multiplier
            // We use a generic multiplier of 50 (standard for many minis) or just points for scoring.
            // For rigorous accuracy, we would need the contract multiplier.
            // Let's assume raw points for the score to keep it generic but consistent.
            decimal profit = priceChange * VirtualPosition * 10m; // Assuming 10 CHF per tick/point roughly

            VirtualPnL += profit;
            DailyVirtualPnL += profit;
        }
    }

    /// <summary>
    /// Helper to update virtual position and charge costs.
    /// </summary>
    protected void SetVirtualPosition(int newPosition)
    {
        if (VirtualPosition != newPosition)
        {
            // Charge cost for the trade
            VirtualPnL -= TransactionCostPerTrade;
            DailyVirtualPnL -= TransactionCostPerTrade;
            VirtualPosition = newPosition;
        }
    }

    public void OnEndOfDay()
    {
        // Calculate daily return %
        double dailyReturn = (double)(DailyVirtualPnL / AssumedCapital);
        DailyReturnsPct.Add(dailyReturn);

        // Reset daily PnL tracker
        DailyVirtualPnL = 0m;

        // Force virtual flat at EOD (Day Trading)
        SetVirtualPosition(0);
    }

    public double GetSharpeRatio()
    {
        if (DailyReturnsPct.Count < 2) return 0.0;

        double mean = DailyReturnsPct.Average();
        double variance = DailyReturnsPct.Select(r => Math.Pow(r - mean, 2)).Average();
        double stdDev = Math.Sqrt(variance);

        if (stdDev < 1e-9) return 0.0;

        // Annualized Sharpe (assuming 252 days)
        return (mean / stdDev) * Math.Sqrt(252);
    }

    public decimal GetVirtualProfit() => VirtualPnL;
}

// ------------------------------------------------------------------------------------------------------
// Concrete Strategies
// ------------------------------------------------------------------------------------------------------

public class MovingAverageCrossover : BaseStrategy
{
    public override string Name => "MA Crossover";
    private ExponentialMovingAverage _fastEma;
    private ExponentialMovingAverage _slowEma;

    public MovingAverageCrossover(QCAlgorithm algo, Future future) : base(algo, future)
    {
        _fastEma = algo.EMA(Symbol, 9, Resolution.Minute);
        _slowEma = algo.EMA(Symbol, 20, Resolution.Minute);
    }

    // CHANGED: Use passed TradeBar directly
    public override void Update(TradeBar bar)
    {
        // 1. Logic
        if (_fastEma.IsReady && _slowEma.IsReady)
        {
            if (_fastEma > _slowEma) SetVirtualPosition(1);
            else if (_fastEma < _slowEma) SetVirtualPosition(-1);
        }

        // 2. Track Performance
        TrackVirtualPerformance(bar);
    }

    public override void ExecuteRealOrders(Symbol activeSymbol)
    {
        // Simply sync real portfolio with virtual decision
        Algo.SetHoldings(activeSymbol, VirtualPosition);
    }
}

public class MeanReversion : BaseStrategy
{
    public override string Name => "Mean Reversion";
    private RelativeStrengthIndex _rsi;
    private BollingerBands _bb;

    public MeanReversion(QCAlgorithm algo, Future future) : base(algo, future)
    {
        _rsi = algo.RSI(Symbol, 14, MovingAverageType.Simple, Resolution.Minute);
        _bb = algo.BB(Symbol, 20, 2m, MovingAverageType.Simple, Resolution.Minute);
    }

    // CHANGED: Use passed TradeBar directly
    public override void Update(TradeBar bar)
    {
        if (_rsi.IsReady && _bb.IsReady)
        {
            decimal close = bar.Close;

            // Logic: Oversold -> Buy, Overbought -> Sell, Mean -> Exit
            if (_rsi < 30 && close < _bb.LowerBand) SetVirtualPosition(1);
            else if (_rsi > 70 && close > _bb.UpperBand) SetVirtualPosition(-1);
            else if (VirtualPosition == 1 && close >= _bb.MiddleBand) SetVirtualPosition(0);
            else if (VirtualPosition == -1 && close <= _bb.MiddleBand) SetVirtualPosition(0);
        }

        TrackVirtualPerformance(bar);
    }

    public override void ExecuteRealOrders(Symbol activeSymbol)
    {
        Algo.SetHoldings(activeSymbol, VirtualPosition);
    }
}

public class OpeningRangeBreakout : BaseStrategy
{
    public override string Name => "ORB";
    private decimal _rangeHigh = 0m;
    private decimal _rangeLow = 0m;
    private DateTime _openTime;
    private TimeSpan _rangeDuration = TimeSpan.FromMinutes(30);

    public OpeningRangeBreakout(QCAlgorithm algo, Future future) : base(algo, future)
    {
        algo.Schedule.On(algo.DateRules.EveryDay(future.Symbol), algo.TimeRules.AfterMarketOpen(future.Symbol, 0), () =>
        {
            _rangeHigh = 0m;
            _rangeLow = 0m;
            // Capture session open time
            _openTime = algo.Time;
        });
    }

    // CHANGED: Use passed TradeBar directly
    public override void Update(TradeBar bar)
    {
        // 1. Build Range
        if (Algo.Time <= _openTime.Add(_rangeDuration))
        {
            if (_rangeHigh == 0m || bar.High > _rangeHigh) _rangeHigh = bar.High;
            if (_rangeLow == 0m || bar.Low < _rangeLow) _rangeLow = bar.Low;

            // No trading during formation
            SetVirtualPosition(0);
        }
        else
        {
            // 2. Breakout Logic
            if (_rangeHigh > 0)
            {
                if (bar.Close > _rangeHigh) SetVirtualPosition(1);
                else if (bar.Close < _rangeLow) SetVirtualPosition(-1);
            }
        }

        TrackVirtualPerformance(bar);
    }

    public override void ExecuteRealOrders(Symbol activeSymbol)
    {
        Algo.SetHoldings(activeSymbol, VirtualPosition);
    }
}
