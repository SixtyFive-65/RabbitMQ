using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;

namespace Consumer
{
    public class QueueConsumer
    {
        public static void Consume(IModel channel, string myQueue)
        {
            channel.QueueDeclare(myQueue,   //Create Queue
                  durable: true,
                  exclusive: false,
                  autoDelete: false,
                  arguments: null
                  );

            var consumer = new EventingBasicConsumer(channel);  // Create consumer

            string consumedMessage = "";

            consumer.Received += (sender, e) =>
            {
                var body = e.Body.ToArray();  // Get Body as byte array
                var message = Encoding.UTF8.GetString(body); // convert message to string

                consumedMessage = message;

                Console.WriteLine(message);
            };

            channel.BasicConsume(myQueue, true, consumer);
            Console.WriteLine("Consumer Started");
            Console.ReadLine();
        }
    }
}
