using LuoliCommon;
using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.DTO.ConsumeInfo.Sexytea;
using LuoliCommon.DTO.Coupon;
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.Enums;
using LuoliUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ThirdApis;
using ThirdApis.Services.ConsumeInfo;
using ThirdApis.Services.Coupon;
using static Azure.Core.HttpHeader;

namespace PlaceOrderBOT
{
    public class SexyteaPlaceOrderBOT : IPlaceOrderBOT
    {
        private readonly SexyteaApis _sexyteaApis;
        private readonly IConsumeInfoRepository _consumeInfoRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly LuoliCommon.Logger.ILogger _logger;
        public SexyteaPlaceOrderBOT(
            SexyteaApis sexyteaApis,
            IConsumeInfoRepository consumeInfoRepository,
            ICouponRepository couponRepository,
              LuoliCommon.Logger.ILogger logger)
        {
            _sexyteaApis = sexyteaApis;
            _consumeInfoRepository = consumeInfoRepository;
            _couponRepository = couponRepository;
            _logger = logger;
        }

        private static JsonSerializerOptions _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, // 关键配置：忽略大小写
        };


        public async Task<(bool, string)> PlaceOrder(CouponDTO coupon, ExternalOrderDTO eo, ConsumeInfoDTO consumeInfoInput)
        {
            _logger.Debug($"starting SexyteaPlaceOrderBOT.PlaceOrder for tid:[{eo.Tid}] coupon:[{coupon.Coupon}]");

            try
            {
                var consumeInfo = JsonSerializer.Deserialize<ConsumeInfoDTO<SexyteaGoods>>(JsonSerializer.Serialize(consumeInfoInput), _options);

                var account =await RedisHelper.GetAsync<Account>(RedisKeys.SexyteaTokenAccount);

                if(account is null)
                {
                    string msg = "SexyteaPlaceOrderBOT.PlaceOrder 代理账号token过期";
                    _logger.Error(msg);
                    await _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.TokenExpired });

                    return (false, msg);
                }

                int branchId = consumeInfo.Goods.BranchId;
                List<OrderItem> orderItems = consumeInfo.Goods.OrderItems;


                //判断是否能够全额使用积点

                int selectPoint = 0;
                try
                {
                    var userInfoStr = await _sexyteaApis.UserInfo(account);
                    JsonDocument userInfoJson = JsonDocument.Parse(userInfoStr);

                    selectPoint = calOrderMode(coupon, userInfoJson, coupon.AvailableBalance);

                    notifyWhileBalanceNotEnough(userInfoJson, 200m);
                }
                catch (Exception ex)
                {
                    _logger.Error($"SexyteaPlaceOrderBOT.PlaceOrder 获取用户信息失败 tid:[{eo.Tid}]");
                    _logger.Error(ex.Message);
                    return (false, "获取用户信息失败");
                }
              

               //创建订单
                var respOrderCreate = await _sexyteaApis.OrderCreate(account, branchId, orderItems, consumeInfo.LastName, consumeInfo.Remark, coupon.CreditLimit, selectPoint);
                if (!respOrderCreate.Item1)
                {
                    _logger.Error($"SexyteaPlaceOrderBOT.PlaceOrder 茶颜订单创建失败 tid:[{eo.Tid}]");
                    await _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.CreateOrderFailed });

                    return (false, respOrderCreate.Item2);
                }

                if(coupon.AvailableBalance < respOrderCreate.Item3)
                {
                    string msg = $"SexyteaPlaceOrderBOT.PlaceOrder 茶颜订单停止下单，创建订单的金额[{respOrderCreate.Item3}] 超出可用余额[{coupon.AvailableBalance}] tid:[{eo.Tid}]";
                    _logger.Error(msg);
                    await _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.CouponBalanceNotEnough });

                    return (false, msg);
                }


                //此时订单已经创建成功
                var orderNo = respOrderCreate.Item2;

                //核算订单付款金额
                var respOrderPayCal = await _sexyteaApis.OrderPayCal(account, orderNo);
                if (!respOrderPayCal.Item1)
                {
                    _logger.Error($"SexyteaPlaceOrderBOT.PlaceOrder 茶颜订单计算付款失败 order_no:[{orderNo}] tid:[{eo.Tid}]");
                    return (false, respOrderPayCal.Item3);
                }

                var payAmount = respOrderPayCal.Item2;

                //全积点支付
                if(payAmount == 0)
                {
                    //付款成功了，coupon可用余额要减掉, 在updateResult里更新
                    coupon.AvailableBalance -= respOrderCreate.Item3;
                    coupon.ProxyOrderId = orderNo;
                    //记录下单的代理账号
                    coupon.ProxyOpenId = account.phone;

                    return (true, string.Empty);
                }


                //付款
                var respOrderPay = await _sexyteaApis.OrderPay(account, orderNo, payAmount);
                if (!respOrderPay.Item1)
                {
                    _logger.Error($"SexyteaPlaceOrderBOT.PlaceOrder 茶颜订单付款失败 order_no:[{orderNo}] tid:[{eo.Tid}]");
                    await _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.AffordOrderFailed });
                    
                    return (false, $"付款失败,{respOrderPay.Item2}");
                }
                //付款成功了，coupon可用余额要减掉, 在updateResult里更新
                coupon.AvailableBalance -= respOrderCreate.Item3;
                coupon.ProxyOrderId = orderNo;
                //记录下单的代理账号
                coupon.ProxyOpenId= account.phone;

                return (true, string.Empty);

            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaPlaceOrderBOT.PlaceOrder");
                _logger.Error(ex.Message);
                return (false, "unknown exception in SexyteaPlaceOrderBOT.PlaceOrder");
            }
        }


        private void notifyWhileBalanceNotEnough(JsonDocument userInfoJson, decimal threshold)
        {
            decimal currentBalance = userInfoJson.RootElement.GetProperty("data").GetProperty("accountInfo").GetProperty("totalBalance").GetDecimal();

            if (currentBalance >= threshold)
                return;

            ApiCaller.NotifyAsync(@$"当前余额:[{currentBalance}] 低于[{threshold}] 请尽快充值" , Program.NotifyUsers);
        }

        private int calOrderMode(CouponDTO coupon, JsonDocument userInfoJson, decimal orderPrice)
        {
            decimal currentAllPoint  = userInfoJson.RootElement.GetProperty("data").GetProperty("accountInfo").GetProperty("fPoint").GetDecimal();
            int selectPoint = 0;


            //积分使用规则
            //1. 积分大于100，全部使用积分
            //2. 积分大于50小于等于100，订单金额大于等于18，使用积分，否则不使用积分
            //3. 积分小于等于50，订单金额大于等于20，使用积分，否则不使用积分

            if (currentAllPoint > 100)
               selectPoint = 1;
            else if (currentAllPoint > 50)
            {
                if (orderPrice >= 18)
                    selectPoint = 1;
                else
                    selectPoint = 0;
            }
            else
            {
                if (orderPrice > 18 && orderPrice <25)
                    selectPoint = 1;
                else
                    selectPoint = 0;
            }
                

            _logger.Info($"卡密[{coupon.Coupon}] 查到积点[{currentAllPoint}] 当前可消费金额[{coupon.AvailableBalance}] 决定{(selectPoint == 0 ? "不" : "")}使用积分");

            return selectPoint;
        }
        public async Task<(bool, string)> UpdateResult(CouponDTO coupon, ExternalOrderDTO eo, ConsumeInfoDTO consumeInfo)
        {
            var updateCouponResp = await _couponRepository.Update(new LuoliCommon.DTO.Coupon.UpdateRequest() { Coupon = coupon, Event = EEvent.Placed});
            if (!updateCouponResp.ok)
            {
                await _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.UpdateCouponFailed });
                return (false, $"CouponDTO Update failed:{updateCouponResp.msg}");
            }
            var updateCIResp = await _consumeInfoRepository.ConsumeInfoUpdate(new LuoliCommon.DTO.ConsumeInfo.UpdateRequest() { CI = consumeInfo, Event = EEvent.Placed });
            if (!updateCIResp.ok)
            {
                await _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.UpdateCIFailed });
                return (false, $"ConsumeInfoDTO Update failed:{updateCouponResp.msg}");
            }

            return (true, string.Empty);
        }

        public (bool, string) Validate(CouponDTO coupon, ExternalOrderDTO eo, ConsumeInfoDTO consumeInfo)
        {
            if (coupon.Status != LuoliCommon.Enums.ECouponStatus.Shipped)
            {
                _= _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon=coupon.Coupon, ErrorCode = ECouponErrorCode.CouponStatusNotMacth}).Result;
                return (false, $"CouponDTO Status:[{coupon.Status.ToString()}], must be [ECouponStatus.Shipped]");
            }
            if (coupon.CreditLimit != coupon.AvailableBalance)
            {
                _ = _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.CouponPaymentNotEqualABalance }).Result;
                return (false, $"CouponDTO CreditLimit[{coupon.CreditLimit}] must be equal to AvailableBalance[{coupon.AvailableBalance}]");
            }
            if (eo.Status == LuoliCommon.Enums.EExternalOrderStatus.Refunding)
            {
                _ = _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.EOReceivedRefund }).Result;
                return (false, $"ExternalOrderDTO Status[{eo.Status.ToString()}], so do not process");
            }
            if (consumeInfo.Status != LuoliCommon.Enums.EConsumeInfoStatus.Pulled)
            {
                _ = _couponRepository.UpdateErrorCode(new UpdateErrorCodeRequest() { Coupon = coupon.Coupon, ErrorCode = ECouponErrorCode.CIStatusNotMacth }).Result;
                return (false, $"ConsumeInfoDTO Status[{consumeInfo.Status.ToString()}] must be equal to [EConsumeInfoStatus.Pulled]");
            }

            return (true, string.Empty);
        }
    }
}
