using Azure;
using LuoliCommon;
using LuoliCommon.DTO.Agiso;
using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.DTO.Coupon;
using LuoliCommon.DTO.ExternalOrder;
using LuoliUtils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.ServiceModel.Channels;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using ThirdApis;
using IChannel = RabbitMQ.Client.IChannel;

namespace PlaceOrderBOT
{
    public class ConsumerService : BackgroundService
    {
        private readonly IChannel _channel;
        private readonly AsynsApis _asynsApis;
        private readonly string _queueName = Program.Config.KVPairs["StartWith"] +  RabbitMQKeys.ConsumeInfoInserted; // 替换为你的队列名
        private readonly LuoliCommon.Logger.ILogger _logger;

        private readonly IPlaceOrderBOT Bot;

        private static JsonSerializerOptions _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, // 关键配置：忽略大小写
        };


        public ConsumerService(IChannel channel,
             AsynsApis asynsApis,
             LuoliCommon.Logger.ILogger logger,
             IPlaceOrderBOT bot
             )
        {
            _channel = channel;
            _logger = logger;
            _asynsApis = asynsApis;
            Bot = bot;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 声明队列
            await _channel.QueueDeclareAsync(
                queue: _queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: stoppingToken);

            // 设置Qos
            await _channel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: 10,
                global: false,
                stoppingToken);

            // 创建消费者
            var consumer = new AsyncEventingBasicConsumer(_channel);

            // 处理接收到的消息
            consumer.ReceivedAsync += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);

                try
                {
                    _logger.Info($"收到:{message}, 开始处理");

                    //这里要重新从数据库获取订单，防止数据不一致
                    //可能收到退款通知什么的

                    var consumeInfo = JsonSerializer.Deserialize<ConsumeInfoDTO>(message, _options);

                    var couponResp = await _asynsApis.CouponQuery(consumeInfo.Coupon);
                    var couponDto = couponResp.data;
                  
                    var eoResp = await _asynsApis.ExternalOrderQuery(couponDto.ExternalOrderFromPlatform, couponDto.ExternalOrderTid);
                    var eoDto = eoResp.data;
                    
                    if (!eoResp.ok && !couponResp.ok)
                    {
                        _logger.Error("订单/卡密查询失败");
                        Notify(couponDto, eoDto, "订单/卡密查询失败", ea.DeliveryTag, stoppingToken);
                        return;
                    }
                    couponDto = couponResp.data;

                    var (validateResult, validateMsg) = Bot.Validate(couponDto, eoDto, consumeInfo);

                    if (!validateResult)
                    {
                        _logger.Error($"订单校验失败:{validateMsg}");
                        Notify(couponDto, eoDto, $"订单校验失败:{validateMsg}", ea.DeliveryTag, stoppingToken);
                        return;
                    }

                    var (placeOrderResult, placeOrderMsg) = await Bot.PlaceOrder(couponDto, eoDto, consumeInfo);
                    if (!placeOrderResult)
                    {
                        _logger.Error($"下单失败:{placeOrderMsg},订单 订单号:{eoDto.Tid}, 已付金额:{eoDto.PayAmount}");
                        Notify(couponDto, eoDto, $"下单失败:{placeOrderMsg}", ea.DeliveryTag, stoppingToken);
                        return;
                    }


                    var (updateResult, updateMsg) = await Bot.UpdateResult(couponDto, eoDto, consumeInfo);
                    if (!updateResult)
                    {
                        _logger.Error($"下单后更新信息失败:{updateMsg},订单 订单号:{eoDto.Tid}, 已付金额:{eoDto.PayAmount}");
                        Notify(couponDto, eoDto, $"下单后更新信息失败:{updateMsg}", ea.DeliveryTag, stoppingToken);
                        return;
                    }


                    _logger.Info($"{Program.Config.ServiceName}订单处理成功 订单号:{eoDto.Tid}, 已付金额:{eoDto.PayAmount}");

                    //通知页面刷新
                    RedisHelper.Publish(RedisKeys.Pub_RefreshPlaceOrderStatus, couponDto.Coupon);

                    // 处理成功，确认消息
                    await _channel.BasicAckAsync(
                            deliveryTag: ea.DeliveryTag,
                            multiple: false,
                            stoppingToken);
              
                }
                catch (Exception ex)
                {
                    _logger.Error("while ConsumerService consuming");
                    _logger.Error(ex.Message);
                    // 处理异常，记录日志
                    // 异常情况下不确认消息，不重新入队
                    await _channel.BasicNackAsync(
                        deliveryTag: ea.DeliveryTag,
                        multiple: false,
                        requeue: false,
                        stoppingToken);

                    ApiCaller.NotifyAsync(
@$"{Program.Config.ServiceName}.{Program.Config.ServiceId}
MQ 消费过程中异常

message:[{message}]", Program.NotifyUsers);
                }
            };

            // 开始消费
            await _channel.BasicConsumeAsync(
                queue: _queueName,
                autoAck: false,
                consumerTag: Program.Config.ServiceName,
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer: consumer,
                stoppingToken);

            // 保持服务运行直到应用程序停止
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }


        private async Task Notify(CouponDTO coupon, ExternalOrderDTO externalOrder, string coreMsg, ulong tag, CancellationToken token)
        {
            var CI = (await _asynsApis.ConsumeInfoQuery(externalOrder.TargetProxy.ToString() + "_consume_info", coupon.Coupon)).data;
            RedisHelper.Publish(RedisKeys.Pub_RefreshPlaceOrderStatus, externalOrder.Tid);

            _asynsApis.CouponUpdate(new LuoliCommon.DTO.Coupon.UpdateRequest()
            {
                Coupon = coupon,
                Event = LuoliCommon.Enums.EEvent.PlaceFailed
            });

            _asynsApis.ConsumeInfoUpdate(new LuoliCommon.DTO.ConsumeInfo.UpdateRequest()
            {
                CI = CI,
                Event = LuoliCommon.Enums.EEvent.PlaceFailed
            }); 

            _channel.BasicNackAsync(
                      deliveryTag: tag,
                      multiple: false,
                      requeue: false,
                      token);


            Program.Notify(
                coupon,
                externalOrder,
                coreMsg);
        }

    }



}
