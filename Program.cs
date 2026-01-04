using LuoliCommon;
using LuoliCommon.DTO.ConsumeInfo.Sexytea;
using LuoliCommon.DTO.Coupon;
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.Enums;
using LuoliCommon.Interfaces;
using LuoliCommon.Logger;
using LuoliHelper.Utils;
using LuoliUtils;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Refit;
using System.Reflection;
using System.ServiceModel.Channels;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThirdApis;
using IChannel = RabbitMQ.Client.IChannel;
using IConnectionFactory = RabbitMQ.Client.IConnectionFactory;

namespace PlaceOrderBOT
{
    internal class Program
    {
        private static ILogger _logger;
        public static Config Config { get; set; }
        private static RabbitMQConnection RabbitMQConnection { get; set; }
        private static RedisConnection RedisConnection { get; set; }



        public static IPlaceOrderBOT Bot;


        public static List<string> NotifyUsers;


        private static bool init()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            bool result = false;
            string configFolder = "/app/PlaceOrderBOT/configs";

#if DEBUG
            configFolder = "debugConfigs";
#endif

            ActionsOperator.TryCatchAction(() =>
            {
                Config = new Config($"{configFolder}/sys.json");


                RabbitMQConnection = new RabbitMQConnection($"{configFolder}/rabbitmq.json");
                RedisConnection = new RedisConnection($"{configFolder}/redis.json");

                var rds = new CSRedis.CSRedisClient(
                    $"{RedisConnection.Host}:{RedisConnection.Port},password={RedisConnection.Password},defaultDatabase={RedisConnection.DatabaseId}");
                RedisHelper.Initialization(rds);

                NotifyUsers = RedisHelper.SMembers(RedisKeys.NotifyUsers).ToList();

                result = true;
            });

            return result;
        }

        public static async Task Main(string[] args)
        {
            #region luoli code

            Environment.CurrentDirectory = AppContext.BaseDirectory;

            if (!init())
            {
                throw new Exception("initial failed; cannot start");
            }

            #endregion


            var refitSetting = RefitSetting.LuoliRefitSettings();


            var services = new ServiceCollection();

            #region add ILogger

            services.AddHttpClient("LokiHttpClient")
                .ConfigureHttpClient(client =>
                {
                    // client.DefaultRequestHeaders.Add("X-Custom-Header", "luoli-app");
                });

            // 添加 luoli的 ILogger   loki logger
            services.AddSingleton<LuoliCommon.Logger.ILogger, LokiLogger>(provider =>
            {
                var httpClient = provider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("LokiHttpClient");

                var dict = new Dictionary<string, string>();
                dict["app"] = Config.ServiceName;

                var loki = new LokiLogger(Config.KVPairs["LokiEndPoint"],
                    dict,
                    httpClient);
                loki.AfterLog = (msg) => Console.WriteLine(msg);
                return loki;
            });

            #endregion

            #region  add rabbitmq

            services.AddSingleton<IConnectionFactory>(provider =>
            {
                return new ConnectionFactory
                {
                    HostName = RabbitMQConnection.Host,
                    Port = RabbitMQConnection.Port,
                    UserName = RabbitMQConnection.UserId,
                    Password = RabbitMQConnection.UserId,
                    VirtualHost = "/",
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
                };
            });

            services.AddSingleton<IConnection>(provider =>
            {
                var factory = provider.GetRequiredService<IConnectionFactory>();
                return factory.CreateConnectionAsync().Result;
            });

            services.AddSingleton<IChannel>(provider =>
            {
                var connection = provider.GetRequiredService<IConnection>();
                return connection.CreateChannelAsync().Result;
            });

            #endregion

           
       
            services.AddScoped<IPlaceOrderBOT, SexyteaPlaceOrderBOT>();

            services.AddScoped<SexyteaApis>();

            #region 注册 Refit部分   4个带数据库的服务

            services.AddRefitClient<IExternalOrderService>(refitSetting)
                .ConfigureHttpClient(c => c.BaseAddress = new Uri($"http://{Config.KVPairs["StartWith"]}external-order-service:8080"));
            services.AddRefitClient<ICouponService>(refitSetting)
                .ConfigureHttpClient(c => c.BaseAddress = new Uri($"http://{Config.KVPairs["StartWith"]}coupon-service:8080"));
            services.AddRefitClient<IConsumeInfoService>(refitSetting)
                .ConfigureHttpClient(c => c.BaseAddress = new Uri($"http://{Config.KVPairs["StartWith"]}consume-info-service:8080"));
            services.AddRefitClient<IUserService>(refitSetting)
                .ConfigureHttpClient(c => c.BaseAddress = new Uri($"http://{Config.KVPairs["StartWith"]}user-service:8080"));

            #endregion

            services.AddSingleton<SexyteaAccRecommend>();

            //消费消息
            services.AddHostedService<ConsumerService>();

            ServiceLocator.Initialize(services.BuildServiceProvider());

            #region luoli code

            // 应用启动后，通过服务容器获取 LokiLogger 实例
            var prov = services.BuildServiceProvider();

            try
            {
                // 获取 LokiLogger 实例
                var lokiLogger = prov.GetRequiredService<LuoliCommon.Logger.ILogger>();

                // 记录启动日志
                lokiLogger.Info($"{Config.ServiceName}启动成功");

                var assembly = Assembly.GetExecutingAssembly();
                var fileVersionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                var fileVersion = fileVersionInfo.FileVersion;

                lokiLogger.Info($"CurrentDirectory:[{Environment.CurrentDirectory}]");
                lokiLogger.Info($"Current File Version:[{fileVersion}]");

#if DEBUG

#else
                await ApiCaller.NotifyAsync($"{Config.ServiceName}.{Config.ServiceId} v{fileVersion} 启动了", NotifyUsers);
#endif

            }
            catch (Exception ex)
            {
                // 启动日志失败时降级输出
                Console.WriteLine($"启动日志记录失败：{ex.Message}");
            }

            #endregion

            int count = 0;
            int successCount = 0;

            var hostedServices = prov.GetServices<IHostedService>();
            foreach (var hostedService in hostedServices)
            {
                // 启动后台服务（触发 StartAsync -> ExecuteAsync）
                await hostedService.StartAsync(CancellationToken.None);
            }

            // 7. 保持程序运行（否则控制台会直接退出）
            Console.WriteLine("按 Ctrl+C 退出...");
            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true; // 取消默认退出行为
                cancellationTokenSource.Cancel(); // 触发 cancellationToken
            };

            // 等待退出信号
            await Task.Delay(Timeout.Infinite, cancellationTokenSource.Token)
                .ContinueWith(_ => Task.CompletedTask); // 忽略取消异常

            // 8. 停止后台服务（优雅退出）
            foreach (var hostedService in hostedServices)
            {
                await hostedService.StopAsync(CancellationToken.None);
            }



            //5秒发一次心跳给茶颜
            //Task.Run(async () =>
            //{
            //    while (true)
            //    {
            //        try
            //        {
            //            var account = RedisHelper.Get<Account>("Agenter.token");
            //            var heartbeatResult = await SexyteaApis.Heartbeat(account);
            //            if (heartbeatResult)
            //                successCount++;
            //            count++;

            //            Console.WriteLine($"{successCount} / {count}");
            //            await Task.Delay(5000);
            //        }
            //        catch (Exception ex)
            //        {
            //            _logger.Warn("while send heartbeat with  SexyteaApis.Heartbeat");
            //            _logger.Warn(ex.Message);
            //        }

            //    }
            //});

            //if (Config.KVPairs["BOTType"] == "Sexytea")
            //    Bot = new SexyteaPlaceOrderBOT();
            //else
            //    throw new Exception($"unknown BOTType:{Config.KVPairs["BOTType"]}");


            Console.ReadLine();
        }


        public static void Notify(CouponDTO coupon, ExternalOrderDTO externalOrder, string coreMsg)
        {
            ApiCaller.NotifyAsync(
                @$"{Config.ServiceName}.{Config.ServiceId}
{coreMsg}

卡密:{coupon.Coupon}
卡密状态:{EnumHandler.GetDescription((ECouponStatus)coupon.Status)}
卡密金额:{coupon.AvailableBalance}/{coupon.Payment}
卡密绑定订单:{coupon.ExternalOrderFromPlatform} - {coupon.ExternalOrderTid}
订单金额:{externalOrder.PayAmount}", NotifyUsers);
        }
    }
}
