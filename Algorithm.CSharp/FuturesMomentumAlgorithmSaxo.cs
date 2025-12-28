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
        // Parameter für die Strategie (Eurex SMI)
        private const string RootSymbol = Futures.Indices.SMI;
        private Symbol _activeContract = null;

        private Future future;
        private Symbol _activeSymbol;

        // Indikatoren
        private ExponentialMovingAverage _emaTrend;
        private IDataConsolidator _trendConsolidator;

        // OPTIMIERUNG: Konsolidierungs-Zeitraum
        // 1 Minute ist ideal für Intraday-Trendfilter (200 EMA = 200 Minuten Rückblick).
        // 5 Minuten wäre für einen 200 EMA zu träge (1000 Minuten Rückblick -> Mehrtages-Trend).
        private readonly TimeSpan _barPeriod = TimeSpan.FromMinutes(1);

        private int _emaWindow = 100;

        // Strategie-Variablen
        private decimal _dailyHigh = 0;
        private decimal _dailyLow = 0;
        private bool _rangeDefined = false;
        private bool _investedToday = false;

        // DAX Stop Loss in Punkten (z.B. 20 Punkte) ist oft besser als %
        // Hier als Beispiel in % (0.2% im DAX sind ca. 30-40 Punkte bei 16000)
        private decimal _stopLossPoints = 30m;
        private decimal _takeProfitPoints = 90m; // CRV 3:1

        // P&L Tracking
        private decimal _lastPortfolioProfit = 0;
        private decimal _lastTotalFees = 0;

        // Zeit-Einstellungen (Berlin Zeit für Eurex)
        // DAX-Future handelt früher, aber Liquidität und ORB-Logik orientieren sich oft am Xetra-Start (09:00)
        private readonly TimeSpan _marketOpen = new TimeSpan(9, 0, 0);
        private readonly TimeSpan _rangeEnd = new TimeSpan(9, 30, 0); // 30 Minuten Opening Range
        private readonly TimeSpan _exitTime = new TimeSpan(17, 30, 0); // Xetra Schlussauktion / Liquidierung

        public override void Initialize()
        {
            SetTimeZone("Europe/Zurich");
            SetAccountCurrency("CHF");

            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Cash);

            _emaWindow = Convert.ToInt32(GetParameter("emaWindow", 40));
            _stopLossPoints = Convert.ToDecimal(GetParameter("stopLossPoints", 20m));
            _takeProfitPoints = Convert.ToDecimal(GetParameter("takeProfitPoints", 100m));
            //_stoplossInital = Convert.ToInt32(GetParameter("stoplossInital", 50));

            if (Config.Get("environment") == "live-interactive")
            {
                var ticker = "SMI";
                var targetExpiry = GetNextQuarterlyExpiry(Time);

                Log($"[Init] Calculated Target Expiry: {targetExpiry.ToShortDateString()}");

                _activeSymbol = QuantConnect.Symbol.CreateFuture(ticker, Market.EUREX, targetExpiry);
                AddFutureContract(_activeSymbol, Resolution.Tick);
            }
            else
            {
                SetStartDate(2020, 1, 1);
                SetEndDate(2025, 10, 31);

                future = AddFuture(Futures.Indices.SMI, Resolution.Tick, dataMappingMode: DataMappingMode.OpenInterest, dataNormalizationMode: DataNormalizationMode.BackwardsRatio);
                future.SetFilter(TimeSpan.Zero, TimeSpan.FromDays(182));

                // Set a security initializer to apply a Fee Model to everything
                SetSecurityInitializer(security =>
                {
                    if (security.Type == SecurityType.Future)
                    {
                        security.SetFeeModel(new InteractiveBrokersFeeModel());
                        //security.SetFeeModel(new SaxoFeeModel());
                    }
                });
            }

            _emaTrend = new ExponentialMovingAverage(_emaWindow);

            // TÄGLICHER ROLLOVER-CHECK: Vor Marktöffnung (08:45) prüfen wir, welcher Kontrakt das meiste OI hat
            Schedule.On(DateRules.EveryDay(RootSymbol), TimeRules.At(8, 45, TimeZones.Berlin), () =>
            {
                UpdateActiveContract();
            });

            // 4. Scheduled Events
            // Liquidierung am Ende des Handelstages (Berlin Zeit)
            Schedule.On(DateRules.EveryDay(RootSymbol), TimeRules.At(_exitTime.Hours, _exitTime.Minutes, TimeZones.Berlin), () =>
            {
                Liquidate();
                _investedToday = false;
                _rangeDefined = false;
                _dailyHigh = 0; // Reset High/Low
                _dailyLow = 0;
            });
        }

        /// <summary>
        /// Wird aufgerufen, wenn neue Tick-Daten eintreffen
        /// </summary>
        public override void OnData(Slice data)
        {
            if (_activeContract == null) return;

            // Prüfen, ob Ticks für unseren Kontrakt vorhanden sind
            if (!data.Ticks.ContainsKey(_activeContract)) return;

            var ticks = data.Ticks[_activeContract];

            // Wir iterieren durch alle Ticks in diesem Slice (können mehrere sein)
            foreach (var tick in ticks)
            {
                // Wir nutzen nur Trades für die Preisfindung der Strategie (Quotes ignorieren wir hier für den Trigger)
                if (tick.TickType != TickType.Trade) continue;

                decimal currentPrice = tick.Price;

                // Aktuelle Zeit in Berlin
                //var exchangeTime = tick.Time.ToUniversalTime().ConvertFromUtc(TimeZones.Berlin).TimeOfDay;
                var exchangeTime = tick.Time.TimeOfDay;

                // --- Phase 1: Außerhalb der Kern-Handelszeiten ---
                if (exchangeTime < _marketOpen || exchangeTime >= _exitTime)
                {
                    continue;
                }

                // --- Phase 2: Opening Range definieren (09:00 bis 09:30 Uhr) ---
                if (exchangeTime >= _marketOpen && exchangeTime < _rangeEnd)
                {
                    if (!_rangeDefined)
                    {
                        _dailyHigh = currentPrice;
                        _dailyLow = currentPrice;
                        _rangeDefined = true;
                    }
                    else
                    {
                        if (currentPrice > _dailyHigh) _dailyHigh = currentPrice;
                        if (currentPrice < _dailyLow) _dailyLow = currentPrice;
                    }
                    continue; // Noch kein Trading
                }

                // --- Phase 3: Trading ---
                // Wichtig: Indikator muss bereit sein (IsReady)
                if (!_emaTrend.IsReady) continue;

                if (!Portfolio.Invested && !_investedToday)
                {
                    // Long Signal
                    if (currentPrice > _dailyHigh && currentPrice > _emaTrend)
                    {
                        SetHoldings(_activeContract, 0.5);
                        _investedToday = true;
                        Debug($"Long Entry (Tick): {currentPrice} > {_dailyHigh}, Date: {tick.EndTime:dd.MM.yyyy HH:mm:ss}");
                    }
                    // Short Signal
                    else if (currentPrice < _dailyLow && currentPrice < _emaTrend)
                    {
                        SetHoldings(_activeContract, -0.5);
                        _investedToday = true;
                        Debug($"Short Entry (Tick): {currentPrice} < {_dailyLow}, Date: {tick.EndTime:dd.MM.yyyy HH:mm:ss}");
                    }
                }

                // --- Phase 4: Risikomanagement (Tick-Genauigkeit) ---
                if (Portfolio.Invested)
                {
                    var holdings = Portfolio[_activeContract];
                    var entryPrice = holdings.AveragePrice;

                    if (holdings.IsLong)
                    {
                        if (currentPrice <= entryPrice - _stopLossPoints)
                        {
                            Liquidate(_activeContract, "Stop Loss Long");
                        }
                        else if (currentPrice >= entryPrice + _takeProfitPoints)
                        {
                            Liquidate(_activeContract, "Take Profit Long");
                        }
                    }
                    else if (holdings.IsShort)
                    {
                        if (currentPrice >= entryPrice + _stopLossPoints)
                        {
                            Liquidate(_activeContract, "Stop Loss Short");
                        }
                        else if (currentPrice <= entryPrice - _takeProfitPoints)
                        {
                            Liquidate(_activeContract, "Take Profit Short");
                        }
                    }
                }
            }
        }

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            // Wenn neue Futures hinzukommen (z.B. am Start oder durch Filter-Änderung),
            // prüfen wir sofort, ob wir den Kontrakt wechseln sollten.
            if (changes.AddedSecurities.Any(s => s.Symbol.SecurityType == SecurityType.Future))
            {
                UpdateActiveContract();
            }
        }

        /// <summary>
        /// Überprüft alle verfügbaren Futures und wählt den mit dem höchsten Open Interest
        /// </summary>
        private void UpdateActiveContract()
        {
            // Wir suchen alle Futures im aktuellen Universum, die zu unserem RootSymbol gehören
            // UND deren Verfallsdatum strikt in der Zukunft liegt (> Time.Date)
            var candidates = ActiveSecurities.Values
                .Where(s => s.Symbol.SecurityType == SecurityType.Future &&
                            s.Symbol.ID.Symbol == RootSymbol &&
                            s.Symbol.ID.Date > Time.Date) // UPDATE: Filter für abgelaufene Kontrakte
                .ToList();

            if (!candidates.Any()) return;

            // Logik: Wähle den Kontrakt mit dem höchsten Open Interest.
            // Falls OI gleich ist (z.B. am Anfang), nimm den mit der kürzesten Laufzeit (Date).
            var bestContract = candidates
                .OrderByDescending(s => s.OpenInterest)
                .ThenBy(s => s.Symbol.ID.Date)
                .FirstOrDefault();

            if (bestContract != null && bestContract.Symbol != _activeContract)
            {
                SwitchToContract(bestContract.Symbol);
            }
        }

        /// <summary>
        /// Führt den technischen Wechsel des Kontrakts durch (Consolidators umhängen, History laden)
        /// </summary>
        private void SwitchToContract(Symbol newSymbol)
        {
            // Alten Consolidator entfernen
            if (_activeContract != null && _trendConsolidator != null)
            {
                SubscriptionManager.RemoveConsolidator(_activeContract, _trendConsolidator);
                _trendConsolidator = null;
                // Optional: Alte Positionen schließen, falls man über den Rollover hält (hier nicht nötig da Daytrading)
                if (Portfolio[_activeContract].Invested) Liquidate(_activeContract);
            }

            _activeContract = newSymbol;
            Debug($"Kontraktwechsel zu: {_activeContract.Value} | OI: {ActiveSecurities[_activeContract].OpenInterest}");

            // Neuen Consolidator erstellen
            _trendConsolidator = new TickConsolidator(_barPeriod);
            RegisterIndicator(_activeContract, _emaTrend, _trendConsolidator);
            SubscriptionManager.AddConsolidator(_activeContract, _trendConsolidator);

            // Indikator "aufwärmen" (Warmup)
            _emaTrend.Reset();
            var history = History(_activeContract.Canonical, _emaWindow * (int)_barPeriod.TotalMinutes, Resolution.Minute);
            foreach (var bar in history)
            {
                _emaTrend.Update(bar.Time, bar.Close);
            }
        }

        // --- NEU: Trade Logging ---
        // Diese Methode wird automatisch vom Framework aufgerufen, wenn sich der Status einer Order ändert.
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            // Wir loggen nur tatsächlich ausgeführte Trades (Filled)
            if (orderEvent.Status == OrderStatus.Filled)
            {
                // Buy oder Sell Text
                var direction = orderEvent.Direction == OrderDirection.Buy ? "BUY" : "SELL";

                // P&L Berechnung inklusive Gebühren
                // Hinweis: Trade Net P&L auf Entry-Seite ist oft negativ (nur Gebühr), 
                // auf Exit-Seite ist es Realisierter Gewinn - Exit-Gebühr.

                var profitDelta = Portfolio.TotalProfit - _lastPortfolioProfit;
                var feesDelta = Portfolio.TotalFees - _lastTotalFees;
                var tradeNetPnL = profitDelta - feesDelta;

                var cumNetPnL = Portfolio.TotalProfit - Portfolio.TotalFees;

                // Update trackers
                _lastPortfolioProfit = Portfolio.TotalProfit;
                _lastTotalFees = Portfolio.TotalFees;

                Debug($"[TRADE EXECUTION] {Time:dd.MM.yyyy HH:mm:ss} | {orderEvent.Symbol} | {direction} | Qty: {orderEvent.FillQuantity} | Price: {orderEvent.FillPrice} | Fees: {orderEvent.OrderFee} | Trade Net P&L: {tradeNetPnL:F2} | Cum Net P&L: {cumNetPnL:F2}");
            }
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
