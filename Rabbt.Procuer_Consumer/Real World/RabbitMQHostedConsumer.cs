using Consumer.Models;
using Flurl.Http;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog;
using System.Text;
using System.Text.Json;

namespace Consumer
{
    public delegate (bool isSuccess, string? message, Exception? exception) CustomMessageHandler<T>(T message, CancellationToken cancellationToken); // Return true to ack message, false for nack
    public delegate Task<(bool isSuccess, string? message, Exception? exception)> CustomAsyncMessageHandler<T>(T message, CancellationToken cancellationToken); // Return true to ack message, false for nack

    public class RabbitMQHostedConsumer : BackgroundService
    {
        private IConnection? _connection;
        private IModel? _channel;

        private readonly IConfiguration _configuration;

        public RabbitMQHostedConsumer(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var rabbitMQUrl = _configuration?.GetSection("RabbitMQUrl")?.Value?.ToString()
                     ?? throw new ArgumentNullException("RabbitMQUrl has not been defined in config.");

                var queueName = _configuration?.GetSection("WithdrawalRulesApi:queueName")?.Value?.ToString()
                    ?? throw new ArgumentNullException("queueName has not been defined in config.");

                Log.Logger.Information("[{serviceName}] Attempting connection to {rabbitMQUrl}", nameof(RabbitMQHostedConsumer), rabbitMQUrl);

                var factory = new ConnectionFactory
                {
                    Uri = new Uri(rabbitMQUrl!),
                    RequestedConnectionTimeout = TimeSpan.FromMinutes(2),
                    SocketReadTimeout = TimeSpan.FromMinutes(1),
                    SocketWriteTimeout = TimeSpan.FromMinutes(1),
                    ContinuationTimeout = TimeSpan.FromMinutes(1),
                    HandshakeContinuationTimeout = TimeSpan.FromMinutes(1),
                    ClientProvidedName = $"RuleValidation.{nameof(RabbitMQHostedConsumer)}",
                    RequestedHeartbeat = new TimeSpan(0, 5, 0)
                };

                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                RegisterAsyncConsumer<WithdrawalRuleResultRabbitDto>(
                     queueName: queueName,
                     consumerName: "RuleValidation",
                     onMessageReceivedEventHandler: SaveRuleValidationResultAsync,
                     autoAck: false
                 );
            }
            catch (Exception ex)
            {
                Log.Error("Something went wrong", nameof(RabbitMQHostedConsumer), ex?.InnerException?.Message);
            }
        }

        private void RegisterAsyncConsumer<T>(string queueName, string consumerName, CustomAsyncMessageHandler<T>? onMessageReceivedEventHandler = default, bool autoAck = true, (string appSettingName, bool defaultValue)? featureflagged = default) where T : class
          => RegisterConsumerInternal(queueName: queueName, consumerName: consumerName, onAsyncMessageReceivedEventHandler: onMessageReceivedEventHandler, autoAck: autoAck, featureflagged: featureflagged);

        internal void RegisterConsumerInternal<T>(
                  string queueName,
                  string consumerName,
                  CustomMessageHandler<T>? onMessageReceivedEventHandler = default,
                  CustomAsyncMessageHandler<T>? onAsyncMessageReceivedEventHandler = default,
                  bool autoAck = true,
                  (string appSettingName, bool defaultValue)? featureflagged = default
        ) where T : class
        {
            _channel!.QueueDeclare
            (
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null
            );

            _channel.BasicQos(prefetchSize: 0, prefetchCount: 50, global: false);

            var consumer = new EventingBasicConsumer(_channel);

            var source = new CancellationTokenSource();

            consumer.Received += async (model, eventArgs) =>
            {
                var message = Encoding.UTF8.GetString(eventArgs.Body.ToArray());

                try
                {
                    if (!string.IsNullOrEmpty(message))
                    {
                        var data = typeof(T) != typeof(string)
                            ? JsonSerializer.Deserialize<T>(message, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            })
                            : message as T;

                        var result = data != default
                            ? onAsyncMessageReceivedEventHandler != default
                                ? await onAsyncMessageReceivedEventHandler!(data, source.Token)
                                : onMessageReceivedEventHandler!(data, source.Token)
                            : (false, "RabbitMqConsumerHostedService: empty message received", new NullReferenceException("RabbitMqConsumerHostedService: empty message received"));

                        // Handle response if needed
                        if (!autoAck)
                        {
                            if (!result.isSuccess)
                            {
                                _channel.BasicNack(eventArgs.DeliveryTag, false, true); // Failed, re-queue

                                if (result.exception != default)
                                {
                                    Log.Error(result.exception, "[{serviceName}.{consumerName}]", nameof(RabbitMQHostedConsumer), consumerName);
                                }
                            }
                            else
                            {
                                _channel.BasicAck(eventArgs.DeliveryTag, false); // We are done
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[{serviceName}.{consumerName}]", nameof(RabbitMQHostedConsumer), consumerName);

                    if (!autoAck)
                    {
                        _channel.BasicNack(eventArgs.DeliveryTag, false, true); // Failed, re-queue
                    }
                }
            };

            consumer.Registered += (s, e) => Log.Logger.Information("[{serviceName}.{consumerName}] Consumer registered, listening for messages on queue: {queueName}.", nameof(RabbitMQHostedConsumer), consumerName, queueName);
            consumer.Unregistered += (s, e) => Log.Logger.Information("[{serviceName}.{consumerName}] Consumer shutting down. Reason: {reason}.", nameof(RabbitMQHostedConsumer), consumerName, consumer.ShutdownReason?.ReplyText ?? "Unknown");

            _channel.BasicConsume(consumer, queueName, autoAck);
        }

        public async Task<(bool isSuccess, string? message, Exception? exception)> SaveRuleValidationResultAsync(WithdrawalRuleResultRabbitDto data, CancellationToken cancellationToken)
        {
            var saveResult = false;

            try
            {
                var _baseUrl = _configuration?.GetSection("WithdrawalRulesApi:BaseUrl")?.Value?.ToString();
                var _apiKey = _configuration?.GetSection("WithdrawalRulesApi:ApiKey")?.Value?.ToString();

                saveResult = await _baseUrl.WithHeader("X-Api-Key", _apiKey)
                                      .AppendPathSegment($"Api/RuleValidation/SaveWithdrawalRules/{"rules"}")
                                      .PostJsonAsync(data)
                                      .ReceiveJson<bool>();

                Log.Logger.Information($"Call to {_baseUrl} returned {saveResult}", nameof(RabbitMQHostedConsumer), _baseUrl);
            }
            catch (Exception ex)
            {
                Log.Error($"Something went wrong {ex?.InnerException?.Message}");

                return BuildResponse(false, ex?.Message, ex);
            }

            if (!saveResult)
            {
                return BuildResponse(false, $"Something went wrong, couldn't save rules for {data.WithdrawalTransactionId}");
            }

            return BuildResponse(true);
        }

        private static (bool isSuccess, string? message, Exception? exception) BuildResponse(bool isSuccess, string? message = default, Exception? exception = default)
             => (isSuccess, message, exception);
    }
}
