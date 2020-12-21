using System;
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Indicators;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp
{
    public class StarterSystem : QCAlgorithm
    {
        private const int StdPeriod = 25;
        private const int FastMAPeriod = 16;
        private const int SlowMAPeriod = 64;

        private const int LONG = 0;
        private const int SHORT = 1;
        private const int NONE = -1;

        private int previousPosition = NONE;
        private int currentPosition = NONE;

        // capital at risk
        private decimal capital = 100000;

        // annual volatility target
        private decimal targetVolatility = 0.12m;

        // instrument volatility multiplier
        // used for calculating the trailing stop gap
        private decimal tradingSpeedFactor = 0.5m;

        // close price where we have made maximum profits
        // used to implement a trailing stop
        private decimal highWatermark = 0.0m;

        // ticket for the trailing stop order
        private OrderTicket trailingStopOrderTicket;

        // ticket for the long or short order
        private OrderTicket orderTicket;

        // Instrument to trade
        private Symbol spy = QuantConnect.Symbol.Create("SPY", SecurityType.Equity, Market.USA);

        RollingWindow<decimal> spyClosePrices;
        ExponentialMovingAverage fastMA, slowMA;

        public override void Initialize()
        {
            SetStartDate(2006, 1, 1);
            SetEndDate(2020, 12, 1);
            SetCash(capital);

            AddSecurity(spy, Resolution.Daily);

            spyClosePrices = new RollingWindow<decimal>(StdPeriod);
            fastMA = EMA(spy, FastMAPeriod, Resolution.Daily);
            slowMA = EMA(spy, SlowMAPeriod, Resolution.Daily);

            SetWarmup(SlowMAPeriod);
        }

        public override void OnData(Slice data)
        {
            spyClosePrices.Add(data[spy].Close);

            if (IsWarmingUp)
            {
                return;
            }

            updateHighWaterMark(data);

            if (Portfolio.Invested)
            {
                updateTrailingStop();
                return; // TODO recalculate position size here because instrument volatility might have changed.
            }

            if ((previousPosition == LONG || previousPosition == NONE) && fastMA < slowMA)
            {
                // go short
                Debug("---");
                Debug("Going short at " + data[spy].Close);

                int numberOfShares = calculatePositionSize(data);

                Debug("number of shares= " + numberOfShares);

                var stopPrice = calculateStopPriceForShort();

                Debug("stop price= " + stopPrice);

                orderTicket = MarketOrder(spy, -numberOfShares);
                trailingStopOrderTicket = StopMarketOrder(spy, numberOfShares, stopPrice);
                currentPosition = SHORT;
            }

            if ((previousPosition == SHORT || previousPosition == NONE) && fastMA > slowMA)
            {
                // go long
                Debug("---");
                Debug("Going Long at " + data[spy].Close);

                int numberOfShares = calculatePositionSize(data);

                Debug("number of shares= " + numberOfShares);

                var stopPrice = calculateStopPriceForLong();

                Debug("stop price= " + stopPrice);

                orderTicket = MarketOrder(spy, numberOfShares);
                trailingStopOrderTicket = StopMarketOrder(spy, -numberOfShares, stopPrice);
                currentPosition = LONG;
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var status = orderEvent.Status;
            if (status != OrderStatus.Filled && status != OrderStatus.UpdateSubmitted)
            {
                return;
            }

            if (trailingStopOrderTicket != null && orderEvent.OrderId == trailingStopOrderTicket.OrderId)
            { 
                if (status == OrderStatus.Filled)
                {
                    Debug("---");
                    Debug("Stop triggered at " + orderEvent.FillPrice);
                    previousPosition = currentPosition;
                    currentPosition = NONE;
                }
            }
        }

        private int calculatePositionSize(Slice data)
        {
            decimal assetVolatility = calculateAnnualStandardDeviation(spyClosePrices);
            decimal assetPrice = data[spy].Close;

            decimal numberOfShares = (capital * (targetVolatility / assetVolatility)) / assetPrice;
            return (int)decimal.Round(numberOfShares);
        }

        private decimal calculateStopPriceForLong()
        {
            var instrumentVolatility = calculateAnnualStandardDeviation(spyClosePrices);

            // what percentage does the price have to move away from the high watermark to trigger the stop
            var stopPercent = instrumentVolatility * tradingSpeedFactor;

            // actual stop price
            var stopPrice = highWatermark * (1 - stopPercent);

            return stopPrice;
        }

        private decimal calculateStopPriceForShort()
        {
            var instrumentVolatility = calculateAnnualStandardDeviation(spyClosePrices);

            // what percentage does the price have to move away from the high watermark to trigger the stop
            var stopPercent = instrumentVolatility * tradingSpeedFactor;

            // actual stop price
            var stopPrice = highWatermark * (1 + stopPercent);

            return stopPrice;
        }

        private decimal calculateAnnualStandardDeviation(RollingWindow<decimal> window)
        {
            List<decimal> dailyReturns = new List<decimal>(window.Count - 1);
            for (int i = window.Count - 1; i > 0; i--)
            {
                decimal percentReturn = (window[i - 1] - window[i]) / window[i];
                dailyReturns.Add(percentReturn);
            }

            decimal sum = 0;
            foreach (decimal x in dailyReturns)
            {
                sum += x;
            }

            decimal mean = sum / dailyReturns.Count;

            decimal variance = 0;
            foreach (decimal x in dailyReturns)
            {
                variance += (x - mean) * (x - mean);
            }

            variance /= (dailyReturns.Count - 1);

            return (decimal)Math.Sqrt((double)variance) * 16;
        }

        private void checkForSplits(Slice data)
        {
            if (data.Splits.ContainsKey(spy))
            {
                Debug("---");
                var split = data.Splits.get(spy);
                if (split.Type == SplitType.Warning)
                {
                    Debug("SPY stock will split next trading day");
                }

                if (split.Type == SplitType.SplitOccurred)
                {
                    Debug("Type = " + split.Type + " SplitFactor = " + split.SplitFactor + " ReferencePrice = " + split.ReferencePrice);
                }
            }
        }

        private void updateHighWaterMark(Slice data)
        {
            var lastClosePrice = data[spy].Close;
            if (currentPosition == NONE)
            {
                highWatermark = lastClosePrice;
            }
            else
            if (currentPosition == LONG && lastClosePrice > highWatermark)
            {
                highWatermark = lastClosePrice;
            }
            else
            if (currentPosition == SHORT && lastClosePrice < highWatermark)
            {
                highWatermark = lastClosePrice;
            }
        }

        private void updateTrailingStop()
        {
            if (currentPosition == NONE)
            {
                return;
            }

            decimal stopPrice = currentPosition == LONG ? calculateStopPriceForLong() : calculateStopPriceForShort();
            trailingStopOrderTicket.UpdateStopPrice(stopPrice);
        }
    }
}