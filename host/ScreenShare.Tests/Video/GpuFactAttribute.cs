namespace ScreenShare.Tests.Video;

/// <summary>Teste que usa a placa de vídeo e um monitor de verdade: só roda com SCREENSHARE_GPU_TESTS=1 (o CI não tem GPU).</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCREENSHARE_GPU_TESTS") != "1")
            Skip = "Precisa de placa de vídeo e de um monitor: defina SCREENSHARE_GPU_TESTS=1 para rodar.";
    }
}
