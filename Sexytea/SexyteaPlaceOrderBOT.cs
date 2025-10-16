using LuoliHelper.DBModels;
using LuoliHelper.Entities.Sexytea;
using LuoliHelper.Enums;
using LuoliHelper.StaticClasses;
using LuoliHelper.ThirdApis;
using LuoliHelper.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PlaceOrderBOT.Sexytea
{
    public class SexyteaPlaceOrderBOT : IPlaceOrderBOT
    {
        public string QueueName { get => RabbitMQKeys.ConsumingOrderChannel; }

        public async Task<(bool, string)> PlaceOrder(MOrder order)
        {
            SLogger.Debug($"starting SexyteaPlaceOrderBOT.PlaceOrder for order_no:[{order.order_no}]");

            try
            {
                var account = RedisHelper.Get<SexyteaAccount>("Agenter.token");
                int branchId = int.Parse(order.consume_branch.Split("-")[0]);
                List<MOrderItem> orderItems = JsonSerializer.Deserialize<List<MOrderItem>>(order.consume_content);
                
                var respOrderCreate = await SexyteaApis.OrderCreate(account, branchId, orderItems, order.receiver_name.Substring(0, 1), order.remark, order.pay_amount / 0.8m);
                if (!respOrderCreate.Item1)
                {
                    SLogger.Error($"SexyteaPlaceOrderBOT.PlaceOrder 茶颜订单创建失败 order_no:[{order.order_no}]");
                    return (false, respOrderCreate.Item2);
                }

                //此时订单已经创建成功
                var orderNo = respOrderCreate.Item2;

                var respOrderPayCal = await SexyteaApis.OrderPayCal(account, orderNo);
                if (!respOrderPayCal.Item1)
                {
                    SLogger.Error($"SexyteaPlaceOrderBOT.PlaceOrder 茶颜订单计算付款失败 order_no:[{order.order_no}]");
                    return (false, respOrderPayCal.Item3);
                }

                var payAmount = respOrderPayCal.Item2;

                var respOrderPay = await SexyteaApis.OrderPay(account, orderNo, payAmount);
                if (!respOrderPay)
                {
                    SLogger.Error($"SexyteaPlaceOrderBOT.PlaceOrder 茶颜订单付款失败 order_no:[{order.order_no}]");
                    return (false, "付款失败");
                }
              

                //还没写完；
                return (true, string.Empty);

            }
            catch (Exception ex)
            {
                SLogger.Error("while SexyteaPlaceOrderBOT.PlaceOrder");
                SLogger.Error(ex.Message);
                return (false, "unknown exception in SexyteaPlaceOrderBOT.PlaceOrder");
            }
        }

        public async Task<(bool, string)> PublishResult(MOrder order)
        {
            //20250915
            // 暂定不需要发布
            // 前端轮询订单状态即可

            return (true, string.Empty);
        }

        public async Task<(bool, string)> UpdateResult(MOrder order)
        {

            throw new NotImplementedException();
        }

        public async Task<(bool, string)> Validate(MOrder order)
        {
            bool result = false;
            string msg = string.Empty;

            if(order is null)
            {
                msg = "order is null, 订单未找到";
                return (result, msg);
            }

            var couponStatus = (ECouponStatus)order.consume_coupon_status;
            if (couponStatus != ECouponStatus.Consuming)
            {
                msg = $"卡密状态:{SEnum2Dict.GetDescription(couponStatus)}, 应该是{SEnum2Dict.GetDescription(ECouponStatus.Consuming)}";
                return (result, msg);
            }

            result = true;
            return (result, msg);
        }
    }
}
