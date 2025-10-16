using LuoliHelper.DBModels;
using LuoliHelper.Entities;
using LuoliHelper.Entities.Sexytea;
using LuoliHelper.Enums;
using LuoliHelper.StaticClasses;
using LuoliHelper.ThirdApis;
using LuoliHelper.Utils;
using PlaceOrderBOT.Sexytea;
using SqlSugar;
using System.Reflection;

namespace PlaceOrderBOT
{
    internal class Program
    {
        public static Config Config;

        public static SqlSugarScope SqlClient;
        public static RabbitMQConnection RabbitMQ;

        public static IPlaceOrderBOT Bot;


        public static List<string> NotifyUsers;


        private static bool init()
        {
            bool result = false;
            string configFolder = "configs";

#if DEBUG
            configFolder = "debugConfigs";
#endif

            ActionsOperator.TryCatchAction(() =>
            {
                Config = new Config($"{configFolder}/sys.json");

                NotifyUsers = Config.KVPairs["NotifyUsers"].Split(',').Select(s=>s.Trim()).Where(s=>!String.IsNullOrEmpty(s)).ToList();

                new RedisConnection($"{configFolder}/redis.json");
                SqlClient = new DBConnection($"{configFolder}/database.json").SqlClient;
                RabbitMQ = new RabbitMQConnection($"{configFolder}/rabbitmq.json");

                result = true;
            });

            return result;
        }

        public static async Task Main(string[] args)
        {
            #region luoli code

            Environment.CurrentDirectory = AppContext.BaseDirectory;

            var assembly = Assembly.GetExecutingAssembly();
            var fileVersionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
            var fileVersion = fileVersionInfo.FileVersion;

            SLogger.Info($"CurrentDirectory:[{Environment.CurrentDirectory}]");
            SLogger.Info($"Current File Version:[{fileVersion}]");


            if (!(args is null) && args.Length > 0 && args[0] == "AutoStart")
            {
                SLogger.WriteInConsole = false;
            }

            SLogger.Debug($"WriteInConsole:[{SLogger.WriteInConsole}]");


            if (!init())
            {
                throw new Exception("initial failed; cannot start");
            }

            #endregion


            await ApiCaller.NotifyAsync($"{Config.ServiceName}.{Config.ServiceId} 启动了", NotifyUsers);

          

            int count = 0;
            int successCount = 0;

            //5秒发一次心跳给茶颜
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var account = RedisHelper.Get<SexyteaAccount>("Agenter.token");
                        var heartbeatResult = await SexyteaApis.Heartbeat(account);
                        if (heartbeatResult)
                            successCount++;
                        count++;

                        Console.WriteLine($"{successCount} / {count}");
                        await Task.Delay(5000);
                    }
                    catch (Exception ex)
                    {
                        SLogger.Warn("while send heartbeat with  SexyteaApis.Heartbeat");
                        SLogger.Warn(ex.Message);
                    }

                }
            });

            if (Config.KVPairs["BOTType"] == "Sexytea")
                Bot = new SexyteaPlaceOrderBOT();
            else
                throw new Exception($"unknown BOTType:{Config.KVPairs["BOTType"]}");


            Action<ulong, string> foundMsg =  (tag, msg) =>
            {
                try
                {

                    SLogger.Info($"发现订单号:{msg}, 开始处理");

                    MOrder order = SqlClient.Queryable<MOrder>().Where(O => O.order_no == msg).First();

                    var (validateResult,validateMsg) =  Bot.Validate(order).Result;
                    if(!validateResult)
                    {
                        SLogger.Error($"订单校验失败:{validateMsg}");
                        Notify(order, $"订单校验失败:{validateMsg}");
                        //通知页面刷新
                        RedisHelper.Publish(RedisKeys.CouponChanged, order.consume_coupon);
                        return;
                    }

                    var (placeResult, placeMsg) =  Bot.PlaceOrder(order).Result;
                    if (!placeResult)
                    {
                        SLogger.Error($"下单失败:{placeMsg},订单 订单号:{order.order_no}, 已付金额:{order.pay_amount}");
                        Notify(order, $"下单失败:{placeMsg}");
                        //通知页面刷新
                        RedisHelper.Publish(RedisKeys.CouponChanged, order.consume_coupon);
                        return;
                    }
                    
                    var (updateResult, updateMsg) =  Bot.UpdateResult(order).Result;
                    if (!updateResult)
                    {
                        SLogger.Error($"更新订单状态失败:{updateMsg},订单 订单号:{order.order_no}, 已付金额:{order.pay_amount}");
                        Notify(order, $"更新订单状态失败:{updateMsg}");
                        //通知页面刷新
                        RedisHelper.Publish(RedisKeys.CouponChanged, order.consume_coupon);
                        return;
                    }

                    SLogger.Info($"订单完成 订单号:{order.order_no}, 已付金额:{order.pay_amount}");
                    //通知页面刷新
                    RedisHelper.Publish(RedisKeys.CouponChanged, order.consume_coupon);
                    RabbitMQ.Channel.BasicAckAsync(tag, false);
                }
                catch(Exception ex)
                {
                    SLogger.Error($"MQ 消费过程中异常，message:[{msg}]");
                    SLogger.Error(ex.Message);
                    ApiCaller.NotifyAsync(
@$"{Config.ServiceName}.{Config.ServiceId}
MQ 消费过程中异常

message:[{msg}]", NotifyUsers);

                }

              
            };

            RabbitMQ.Subscribe(Bot.QueueName, foundMsg);

            Console.ReadLine();
        }


        private static void Notify(MOrder order, string coreMsg)
        {
            ApiCaller.NotifyAsync(
@$"{Config.ServiceName}.{Config.ServiceId}
{coreMsg}

订单号:{order.order_no}
订单状态:{SEnum2Dict.GetDescription((EOrderStatus)order.order_status)}
卡密状态:{SEnum2Dict.GetDescription((ECouponStatus)order.consume_coupon_status)}
已付金额:{order.pay_amount}", NotifyUsers);
        }
    }
}
