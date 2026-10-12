using System.Text;
using CodeBridge.HomeAssistant;
using CodeBridge.Transport.Discovery;

namespace CodeBridge.HomeAssistant.Tests;

public class MdnsTests
{
    private sealed class Packet
    {
        private readonly List<byte> _bytes = new() { 0, 0, 0x84, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        private int _records;

        public Packet Name(string name)
        {
            foreach (var label in name.Split('.'))
            {
                var text = Encoding.UTF8.GetBytes(label);
                _bytes.Add((byte)text.Length);
                _bytes.AddRange(text);
            }

            _bytes.Add(0);
            return this;
        }

        public Packet Record(string name, int type, byte[] data)
        {
            Name(name);
            _bytes.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120 });
            _bytes.Add((byte)(data.Length >> 8));
            _bytes.Add((byte)data.Length);
            _bytes.AddRange(data);
            _records++;
            return this;
        }

        public byte[] Build()
        {
            var array = _bytes.ToArray();
            array[7] = (byte)_records; // answer count
            return array;
        }
    }

    private static byte[] NameBytes(string name) => new Packet().Name(name).Build()[12..];

    private static byte[] Txt(params string[] entries) =>
        entries.SelectMany(e => new[] { (byte)Encoding.UTF8.GetByteCount(e) }.Concat(Encoding.UTF8.GetBytes(e))).ToArray();

    [Fact]
    public void A_Home_Assistant_announcement_becomes_an_instance_with_its_address()
    {
        var packet = new Packet()
            .Record("_home-assistant._tcp.local", 12, NameBytes("Casa._home-assistant._tcp.local"))
            .Record("Casa._home-assistant._tcp.local", 33, new byte[] { 0, 0, 0, 0, 0x1F, 0xBB }.Concat(NameBytes("homeassistant.local")).ToArray())
            .Record("Casa._home-assistant._tcp.local", 16, Txt("base_url=http://192.168.1.10:8123", "version=2026.9.0", "location_name=Mi casa"))
            .Record("homeassistant.local", 1, new byte[] { 192, 168, 1, 10 })
            .Build();

        var instances = HomeAssistantFinder.ToInstances(MdnsParser.Parse(packet));

        var instance = Assert.Single(instances);
        Assert.Equal("Mi casa", instance.Name);
        Assert.Equal("192.168.1.10", instance.Address);
        Assert.Equal(8123, instance.Port);
        Assert.Equal("2026.9.0", instance.Version);
        Assert.Equal("http://192.168.1.10:8123", instance.BaseUrl);
        Assert.Equal("192.168.1.10", instance.BrokerHost);
    }

    [Fact]
    public void Other_services_are_ignored()
    {
        var packet = new Packet()
            .Record("_printer._tcp.local", 12, NameBytes("Office._printer._tcp.local"))
            .Build();

        Assert.Empty(HomeAssistantFinder.ToInstances(MdnsParser.Parse(packet)));
    }

    [Fact]
    public void Random_and_truncated_packets_never_throw()
    {
        var random = new Random(1);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);
            MdnsParser.Parse(bytes);
        }

        var valid = new Packet().Record("_home-assistant._tcp.local", 12, NameBytes("Casa._home-assistant._tcp.local")).Build();
        for (var cut = 0; cut < valid.Length; cut++)
            MdnsParser.Parse(valid[..cut]);

        // a compression pointer that points at itself must not loop forever
        MdnsParser.Parse(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 1, 0, 2, 0xC0, 12 });
    }

    [Fact]
    public async Task Looking_for_Home_Assistant_on_a_quiet_network_returns_nothing_without_failing()
    {
        var instances = await HomeAssistantFinder.FindAsync(TimeSpan.FromMilliseconds(300));

        Assert.NotNull(instances);
    }

    [Fact]
    public void The_query_asks_for_the_Home_Assistant_service()
    {
        var query = MdnsClient.BuildQuery("_home-assistant._tcp.local");

        Assert.Equal(1, query[5]); // one question
        Assert.Contains("home-assistant", Encoding.ASCII.GetString(query));
    }
}
