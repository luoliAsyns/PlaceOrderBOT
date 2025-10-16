using LuoliHelper.DBModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PlaceOrderBOT
{
    internal interface IPlaceOrderBOT
    {

        string  QueueName { get; }

        Task<(bool, string)> Validate(MOrder order);
        Task<(bool, string)> PlaceOrder(MOrder order);
        Task<(bool, string)> UpdateResult(MOrder order);
        Task<(bool, string)> PublishResult(MOrder order);

    }
}
