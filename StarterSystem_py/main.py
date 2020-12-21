from QuantConnect import *
from QuantConnect.Parameters import *
from QuantConnect.Benchmarks import *
from QuantConnect.Brokerages import *
from QuantConnect.Util import *
from QuantConnect.Interfaces import *
from QuantConnect.Algorithm import *
from QuantConnect.Algorithm.Framework import *
from QuantConnect.Algorithm.Framework.Selection import *
from QuantConnect.Algorithm.Framework.Alphas import *
from QuantConnect.Algorithm.Framework.Portfolio import *
from QuantConnect.Algorithm.Framework.Execution import *
from QuantConnect.Algorithm.Framework.Risk import *
from QuantConnect.Indicators import *
from QuantConnect.Data import *
from QuantConnect.Data.Consolidators import *
from QuantConnect.Data.Custom import *
from QuantConnect.Data.Fundamental import *
from QuantConnect.Data.Market import *
from QuantConnect.Data.UniverseSelection import *
from QuantConnect.Notifications import *
from QuantConnect.Orders import *
from QuantConnect.Orders.Fees import *
from QuantConnect.Orders.Fills import *
from QuantConnect.Orders.Slippage import *
from QuantConnect.Scheduling import *
from QuantConnect.Securities import *
from QuantConnect.Securities.Equity import *
from QuantConnect.Securities.Forex import *
from QuantConnect.Securities.Interfaces import *
from datetime import date, datetime, timedelta
from QuantConnect.Python import *
from QuantConnect.Storage import *
QCAlgorithmFramework = QCAlgorithm
QCAlgorithmFrameworkBridge = QCAlgorithm
import numpy as np

class StarterSystem(QCAlgorithm):

    def Initialize(self):
        self.StdPeriod = 25
        self.FastMAPeriod = 16
        self.SlowMAPeriod = 64

        self.LONG = 0
        self.SHORT = 1
        self.NONE = -1

        self.previousPosition = self.NONE
        self.currentPosition = self.NONE

        self.capital = 100000

        self.targetVolatility = 0.12
        self.tradingSpeedFactor = 0.5

        self.highWatermark = 0.0

        self.trailingStopOrderTicket: OrderTicket = None
        self.orderTicket: OrderTicket = None

        self.spy = Symbol.Create("SPY", SecurityType.Equity, "USA")
        self.AddSecurity(self.spy, Resolution.Daily)

        self.spyClosePrices = RollingWindow[Decimal](self.StdPeriod)
        self.fastMA = self.EMA(self.spy, self.FastMAPeriod, Resolution.Daily)
        self.slowMA = self.EMA(self.spy, self.SlowMAPeriod, Resolution.Daily)
    
        self.SetStartDate(2006, 1, 1)
        self.SetEndDate(2020, 12, 1)
        self.SetCash(self.capital)  

        self.SetWarmUp(self.SlowMAPeriod)

    def OnData(self, data):
        self.spyClosePrices.Add(data[self.spy].Close)
        
        if self.IsWarmingUp:
            return

        self.updateHighWaterMark(data)

        if self.Portfolio.Invested:
            self.updateTrailingStop()
            return 
        
        if (self.previousPosition == self.LONG or self.previousPosition == self.NONE) and self.fastMA < self.slowMA:
            #short
            numberOfShares = self.calculatePositionSize(data)
            stopPrice = self.calculateStopPriceForShort()
            self.Debug("---")
            self.Debug("Short at " + str(data[self.spy].Close) + " | numShares=" +str(numberOfShares) + " | stopPrice="+str(stopPrice))
            self.orderTicket = self.MarketOrder(self.spy, -numberOfShares)
            self.trailingStopOrderTicket = self.StopMarketOrder(self.spy, numberOfShares, stopPrice)
            self.currentPosition = self.SHORT
        
        elif (self.previousPosition == self.SHORT or self.previousPosition == self.NONE) and self.fastMA > self.slowMA:
            #long
            numberOfShares = self.calculatePositionSize(data)
            stopPrice = self.calculateStopPriceForLong()
            self.Debug("---")
            self.Debug("Long at " + str(data[self.spy].Close) + " | numShares=" +str(numberOfShares) + " | stopPrice="+str(stopPrice))
            self.orderTicket = self.MarketOrder(self.spy, numberOfShares)
            self.trailingStopOrderTicket = self.StopMarketOrder(self.spy, -numberOfShares, stopPrice)
            self.currentPosition = self.LONG

    
    def OnOrderEvent(self, orderEvent: OrderEvent):
        if self.trailingStopOrderTicket != None and self.trailingStopOrderTicket.OrderId == orderEvent.OrderId and orderEvent.Status == OrderStatus.Filled:
            self.Debug("---")
            self.Debug("Stop triggered at " + str(orderEvent.FillPrice))
            self.previousPosition = self.currentPosition
            self.currentPosition = self.NONE

    def calculatePositionSize(self, data):
        assetVolatility = self.calculateAnnualStandardDeviation(self.spyClosePrices)
        assetPrice = data[self.spy].Close
        numberOfShares = (self.capital *(self.targetVolatility)/assetVolatility) / assetPrice
        return round(numberOfShares)

    def calculateAnnualStandardDeviation(self, window: RollingWindow[Decimal]):
        closePrices = np.array([x for x in window])
        closePrices = np.flip(closePrices)
        dailyPercentChanges = np.diff(closePrices) / closePrices[:-1]
        annualizedStandardDeviation = dailyPercentChanges.std(ddof=1) * 16
        return annualizedStandardDeviation

    def calculateStopPriceForLong(self):
        instrumentVolatility = self.calculateAnnualStandardDeviation(self.spyClosePrices)
        stopPercent = instrumentVolatility * self.tradingSpeedFactor
        return self.highWatermark * (1 - stopPercent)

    def calculateStopPriceForShort(self):
        instrumentVolatility = self.calculateAnnualStandardDeviation(self.spyClosePrices)
        stopPercent = instrumentVolatility * self.tradingSpeedFactor
        return self.highWatermark * (1 + stopPercent)

    def updateHighWaterMark(self, data):
        lastClosePrice = data[self.spy].Close
        if self.currentPosition == self.NONE:
            self.highWatermark = lastClosePrice    
       
        elif self.currentPosition == self.LONG and lastClosePrice > self.highWatermark:
            self.highWatermark = lastClosePrice
            
        elif self.currentPosition == self.SHORT and lastClosePrice < self.highWatermark:
            self.highWatermark = lastClosePrice
    
    def updateTrailingStop(self):
        if self.currentPosition == self.NONE:
            return 

        stopPrice = 0
        if self.currentPosition == self.LONG:
            stopPrice = self.calculateStopPriceForLong()
        
        else:
            stopPrice = self.calculateStopPriceForShort()
        
        self.trailingStopOrderTicket.UpdateStopPrice(stopPrice)
        return