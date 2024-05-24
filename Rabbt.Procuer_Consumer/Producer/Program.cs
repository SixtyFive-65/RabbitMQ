using Producer;
using RabbitMQ.Client;
{
    var myQueue = "MX_Rabbit_Demo";
    var rabbitUrl = "amqp://dev:qfEzvoWeNFMAX82I9jos@172.27.215.81:5672/dev";

    var factory = new ConnectionFactory()   //Create connection factory
    {
        Uri = new Uri(rabbitUrl)
    };

    using var connection = factory.CreateConnection();  //Create connection
    using var channel = connection.CreateModel();    //Create Channel

    QueueProducer.Publish(channel, myQueue);
}