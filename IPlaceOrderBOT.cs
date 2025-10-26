using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.DTO.Coupon;
using LuoliCommon.DTO.ExternalOrder;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PlaceOrderBOT
{
    public interface IPlaceOrderBOT
    {
        (bool, string) Validate(CouponDTO coupon, ExternalOrderDTO eo, ConsumeInfoDTO consumeInfo);
        Task<(bool, string)> PlaceOrder(CouponDTO coupon, ExternalOrderDTO eo, ConsumeInfoDTO consumeInfo);
        Task<(bool, string)> UpdateResult(CouponDTO coupon, ExternalOrderDTO eo, ConsumeInfoDTO consumeInfo);

    }
}
