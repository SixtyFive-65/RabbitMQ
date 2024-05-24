using Newtonsoft.Json;
using RabbitMQ.Client;
using System.Text;

namespace Producer
{
    public class QueueProducer
    {
        public static void Publish(IModel channel, string myQueue)
        {
            channel.QueueDeclare(myQueue,   //Create Queue
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null
                );

            var count = 0;

            while (true)  // Produce multiple messages
            {
                var message = new { Name = "Producer", Message = $"I'm producing on Rabbit {count}" };

                var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));  // convert message to byte array

                channel.BasicPublish("", myQueue, null, body);
                count++;
                Thread.Sleep(1000);    //wait a second
            }
        }
    }
}
