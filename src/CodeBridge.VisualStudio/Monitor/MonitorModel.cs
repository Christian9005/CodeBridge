#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeBridge.VisualStudio.Monitor
{
    /// <summary>
    /// Turns a line of serial output into plottable numbers, the way the Arduino Serial Plotter does:
    /// <c>a:1,b:2</c>, <c>temp=23.5 hum=48</c> (named series) or <c>1,2,3</c> / <c>1 2 3</c> (unnamed columns v1, v2, v3).
    /// A line that is not made of numbers is ordinary text and plots nothing.
    /// </summary>
    internal static class PlotParser
    {
        private const string Number = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";

        private static readonly Regex Named = new Regex(
            @"(?<name>[A-Za-z_][A-Za-z0-9_.\-]*)\s*[:=]\s*(?<value>" + Number + @")(?![A-Za-z0-9_.])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Bare = new Regex("^" + Number + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static IReadOnlyList<KeyValuePair<string, double>> Parse(string? line)
        {
            var result = new List<KeyValuePair<string, double>>();
            if (string.IsNullOrWhiteSpace(line))
                return result;

            var named = Named.Matches(line);
            if (named.Count > 0)
            {
                foreach (Match match in named)
                {
                    if (double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && IsFinite(value))
                        result.Add(new KeyValuePair<string, double>(match.Groups["name"].Value, value));
                }

                return result;
            }

            var tokens = line.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length > 16 || !tokens.All(token => Bare.IsMatch(token)))
                return result; // free text, or too many columns to be a plot

            for (var i = 0; i < tokens.Length; i++)
            {
                if (double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && IsFinite(value))
                    result.Add(new KeyValuePair<string, double>("v" + (i + 1).ToString(CultureInfo.InvariantCulture), value));
            }

            return result;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>The text of the monitor: keeps the newest lines and drops the oldest, so memory never grows.</summary>
    internal sealed class LogBuffer
    {
        private readonly Queue<string> _lines = new Queue<string>();

        public LogBuffer(int capacity = 5000)
        {
            Capacity = Math.Max(10, capacity);
        }

        public int Capacity { get; }

        public int Count => _lines.Count;

        /// <summary>Adds a line and returns how many of the oldest lines were dropped to make room.</summary>
        public int Add(string line)
        {
            _lines.Enqueue(line);
            var dropped = 0;
            while (_lines.Count > Capacity)
            {
                _lines.Dequeue();
                dropped++;
            }

            return dropped;
        }

        public void Clear() => _lines.Clear();

        public string Text => string.Join(Environment.NewLine, _lines);

        public IReadOnlyList<string> Lines => _lines.ToList();
    }

    /// <summary>The series of the plotter: one bounded history per name, aligned on a shared sample counter.</summary>
    internal sealed class PlotData
    {
        private readonly Dictionary<string, Queue<double>> _series = new Dictionary<string, Queue<double>>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public PlotData(int window = 300, int maxSeries = 12)
        {
            Window = Math.Max(10, window);
            MaxSeries = Math.Max(1, maxSeries);
        }

        public int Window { get; }

        public int MaxSeries { get; }

        /// <summary>Number of samples (lines that contained numbers) seen so far.</summary>
        public long Samples { get; private set; }

        public IReadOnlyList<string> Names => _order;

        public IReadOnlyList<double> Values(string name) =>
            _series.TryGetValue(name, out var queue) ? queue.ToList() : (IReadOnlyList<double>)Array.Empty<double>();

        /// <summary>Adds the numbers of one line. Returns false when the line had none.</summary>
        public bool Add(IReadOnlyList<KeyValuePair<string, double>> values)
        {
            if (values.Count == 0)
                return false;

            Samples++;
            foreach (var pair in values)
            {
                if (!_series.TryGetValue(pair.Key, out var queue))
                {
                    if (_series.Count >= MaxSeries)
                        continue; // a noisy log cannot create unlimited series

                    queue = new Queue<double>();
                    _series[pair.Key] = queue;
                    _order.Add(pair.Key);
                }

                queue.Enqueue(pair.Value);
                while (queue.Count > Window)
                    queue.Dequeue();
            }

            return true;
        }

        public void Clear()
        {
            _series.Clear();
            _order.Clear();
            Samples = 0;
        }

        /// <summary>Smallest and largest value currently drawn, widened a little so a flat line is visible.</summary>
        public (double Min, double Max) Range()
        {
            var any = false;
            var min = double.MaxValue;
            var max = double.MinValue;
            foreach (var queue in _series.Values)
            {
                foreach (var value in queue)
                {
                    any = true;
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }

            if (!any)
                return (0, 1);

            if (Math.Abs(max - min) < 1e-9)
                return (min - 1, max + 1);

            var margin = (max - min) * 0.05;
            return (min - margin, max + margin);
        }
    }

    /// <summary>
    /// A serial port can only be open in one place. The monitor registers the port it holds so that running a flow or uploading
    /// firmware on that port can ask the monitor to let go first, instead of failing with "port in use".
    /// </summary>
    internal static class SerialPortLease
    {
        private static readonly Dictionary<string, Action> Holders = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new object();

        public static void Acquire(string port, Action release)
        {
            lock (Gate)
                Holders[port] = release;
        }

        public static void Forget(string port)
        {
            lock (Gate)
                Holders.Remove(port);
        }

        /// <summary>Asks whoever holds the port to close it. Returns true when somebody did.</summary>
        public static bool Release(string port)
        {
            Action? release;
            lock (Gate)
            {
                if (!Holders.TryGetValue(port, out release))
                    return false;

                Holders.Remove(port);
            }

            release();
            return true;
        }
    }
}
