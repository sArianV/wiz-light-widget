using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WizLightWidget.Services;

/// <summary>
/// Captura el audio que suena en la PC (loopback WASAPI, sin micrófono) y detecta
/// golpes de bajo/bombo (beats) para poder pulsar luces al ritmo de la música.
///
/// Algoritmo: filtra la señal con un pasa-bajos (~150 Hz) para aislar el bajo,
/// calcula la energía instantánea de cada bloque de audio y la compara contra el
/// promedio y la varianza del último segundo. Cuando la energía instantánea supera
/// ese umbral adaptativo, se considera un beat (con un cooldown mínimo para no
/// disparar varias veces sobre el mismo golpe).
/// </summary>
public class AudioReactiveService : IDisposable
{
    /// <summary>Se dispara una vez por beat detectado, con una intensidad relativa 0-1.</summary>
    public event Action<double>? BeatDetected;

    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinBeatInterval = TimeSpan.FromMilliseconds(200); // tope ~300 BPM

    private WasapiLoopbackCapture? _capture;
    private readonly Queue<(DateTime Time, double Energy)> _energyHistory = new();
    private double _lowPassState;
    private double _alpha;
    private DateTime _lastBeatTime = DateTime.MinValue;

    public void Start()
    {
        if (_capture != null) return;

        _capture = new WasapiLoopbackCapture();

        const double cutoffHz = 150.0;
        double rc = 1.0 / (2 * Math.PI * cutoffHz);
        double dt = 1.0 / _capture.WaveFormat.SampleRate;
        _alpha = dt / (rc + dt);

        _lowPassState = 0;
        _energyHistory.Clear();
        _lastBeatTime = DateTime.MinValue;

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { /* ya detenido */ }
            _capture.Dispose();
            _capture = null;
        }

        _energyHistory.Clear();
        _lowPassState = 0;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format == null) return;

        int sampleCount;
        Func<int, float> getSample;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            sampleCount = e.BytesRecorded / 4;
            getSample = i => BitConverter.ToSingle(e.Buffer, i * 4);
        }
        else if (format.BitsPerSample == 16)
        {
            sampleCount = e.BytesRecorded / 2;
            getSample = i => BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }
        else
        {
            return;
        }

        if (sampleCount == 0) return;

        double sumSquares = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double x = getSample(i);
            _lowPassState += _alpha * (x - _lowPassState);
            sumSquares += _lowPassState * _lowPassState;
        }

        EvaluateBeat(sumSquares / sampleCount);
    }

    private void EvaluateBeat(double instantEnergy)
    {
        var now = DateTime.UtcNow;
        _energyHistory.Enqueue((now, instantEnergy));
        while (_energyHistory.Count > 0 && now - _energyHistory.Peek().Time > HistoryWindow)
            _energyHistory.Dequeue();

        if (_energyHistory.Count < 8) return; // todavía no hay suficiente historial

        double avg = _energyHistory.Average(x => x.Energy);
        if (avg <= 1e-9) return;

        double variance = _energyHistory.Sum(x => (x.Energy - avg) * (x.Energy - avg)) / _energyHistory.Count;

        // Coeficiente de variación (desvío / promedio): a diferencia de la varianza
        // "cruda", es independiente de la escala absoluta de la señal (que acá es
        // amplitud normalizada -1..1, muy chica), así que sí se adapta de verdad.
        double coeffOfVariation = Math.Sqrt(variance) / avg;
        double sensitivity = 1.3 + Math.Clamp(coeffOfVariation, 0, 1.0) * 0.7; // 1.3 a 2.0
        double threshold = sensitivity * avg;

        if (instantEnergy > threshold && now - _lastBeatTime >= MinBeatInterval)
        {
            _lastBeatTime = now;

            // La "fuerza" del golpe se mide relativa al golpe más fuerte del último
            // segundo, para que haya variedad real entre golpes suaves y fuertes
            // en vez de saturar siempre cerca del máximo.
            double historyMax = _energyHistory.Max(x => x.Energy);
            double range = historyMax - threshold;
            double strength = range > 1e-9 ? Math.Clamp((instantEnergy - threshold) / range, 0, 1) : 0.4;
            BeatDetected?.Invoke(strength);
        }
    }

    public void Dispose() => Stop();
}
