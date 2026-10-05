using System.Globalization;
using ScreenShare.Core.Protocol;
using ScreenShare.Video;

namespace ScreenShare.DevHost;

/// <summary>Opção de linha de comando inválida; a mensagem vai para o usuário.</summary>
public sealed class OptionsException(string message) : Exception(message);

/// <summary>As opções do DevHost (fora os comandos do driver).</summary>
public sealed record DevHostOptions(bool NoMonitor, bool NoVideo, bool CapturePrimary, string? RecordPath, VideoOptions Video)
{
    public const string Usage =
        "Opções: --sem-monitor, --sem-video, --capturar principal, --gravar <arquivo>, --fps <1-120>, --bitrate <Mbps>, --codec h264|h265|auto";

    public static DevHostOptions Parse(IReadOnlyList<string> args)
    {
        bool noMonitor = false, noVideo = false, capturePrimary = false;
        string? record = null;
        int fps = 60;
        int? bitrate = null;
        var codec = VideoCodec.None;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--sem-monitor":
                    noMonitor = true;
                    break;
                case "--sem-video":
                    noVideo = true;
                    break;
                case "--capturar":
                    if (Value(args, ref i, "--capturar") != "principal")
                        throw new OptionsException("--capturar só aceita \"principal\" (o monitor principal, para depurar sem o driver).");
                    capturePrimary = true;
                    break;
                case "--gravar":
                    record = Value(args, ref i, "--gravar");
                    break;
                case "--fps":
                    fps = Number(Value(args, ref i, "--fps"), "--fps", 1, 120);
                    break;
                case "--bitrate":
                    bitrate = Number(Value(args, ref i, "--bitrate"), "--bitrate", 1, 500);
                    break;
                case "--codec":
                    codec = Value(args, ref i, "--codec") switch
                    {
                        "h264" => VideoCodec.H264,
                        "h265" => VideoCodec.H265,
                        "auto" => VideoCodec.None,
                        _ => throw new OptionsException("--codec aceita h264, h265 ou auto."),
                    };
                    break;
                default:
                    throw new OptionsException($"Opção desconhecida: {args[i]}");
            }
        }
        return new DevHostOptions(noMonitor, noVideo, capturePrimary, record, new VideoOptions(fps, bitrate, codec));
    }

    private static string Value(IReadOnlyList<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Count) throw new OptionsException($"{option} precisa de um valor.");
        return args[++i];
    }

    private static int Number(string text, string option, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new OptionsException($"{option} precisa de um número de {min} a {max}.");
        return value;
    }
}
