using FFMpegCore.Enums;
using FFMpegCore;
using Polly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace JPB.AudioSubtitleExpander;

public class ExtractSubStreams : WorkerBase
{
	public override async Task Run()
	{
		Console.WriteLine("Source Directory:");
		var sourceDirectory = Environment.GetEnvironmentVariable("ASE_SourceDirectory");
		if (!string.IsNullOrWhiteSpace(sourceDirectory))
		{
			Console.WriteLine($"[From Environment] {sourceDirectory}");
		}
		else
		{
			sourceDirectory = Console.ReadLine();
		}

		if (!Directory.Exists(sourceDirectory))
		{
			Console.WriteLine("Directory does not exist.");
			return;
		}


		//var videoCodecs = FFMpeg.GetCodecs(CodecType.Video);
		//var audoCodecs = FFMpeg.GetCodecs(CodecType.Audio);
		var subtitleCodecs = FFMpeg.GetCodecs(CodecType.Subtitle);

		//var supportedContainers = FFMpeg.GetContainerFormats();


		string[] ImageBasedSubtitles = new[]
		{
			"dvbsub",
			"dvd_sub",
			"dvdsub",
			"pgssub",
			"xsub",
		};
		string[] TextBasedSubtitles = new[]
		{
			"ssa,ass",
			"ass",
			"ssa",
			"webvtt",
			"jacosub",
			"microdvd",
			"mov_text",
			"mpl2",
			"pjs",
			"realtext",
			"sami",
			"stl",
			"subrip",
			"subviewer",
			"subviewer1",
			"text",
			"vplayer",
			"webvtt",
		};
		ImageBasedSubtitles = subtitleCodecs.Where(e => ImageBasedSubtitles.Contains(e.Name.ToLower()))
			.Select(e => e.Name).ToArray();
		TextBasedSubtitles = subtitleCodecs.Where(e => TextBasedSubtitles.Contains(e.Name.ToLower()))
			.Select(e => e.Name).ToArray();
		//var TextBasedSubtitles = subtitleCodecs.Where(e => !e.Type.HasFlag(CodecType.Video)).Select(e => e.Name).ToArray();

		string[] extensions = { "mkv", "m4v", "mp4" };

		var files = Directory.GetFiles(sourceDirectory)
			.Where(e => extensions.Contains(Path.GetExtension(e).Trim('.').ToLower()))
			.ToArray();

		Console.WriteLine($"Found {files.Length}.");

		for (var index = 0; index < files.Length; index++)
		{
			var file = files[index];
			Console.WriteLine($"Run {index}/{files.Length}");
			await ExtractSubMedia(file);
		}

		Console.WriteLine("Done.");

		async Task ExtractSubMedia(string path)
		{
			var mediaInfo = await Policy.Handle<Exception>()
				.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(5 + i))
				.ExecuteAsync(async () => await FFProbe.AnalyseAsync(path));

			for (var index = 0; index < mediaInfo.SubtitleStreams.Count; index++)
			{
				var mediaInfoSubtitleStream = mediaInfo.SubtitleStreams[index];
				Console.Write(
					$"Subtitle {index}/{mediaInfo.SubtitleStreams.Count}. {mediaInfoSubtitleStream.CodecName} {mediaInfoSubtitleStream.Language}");
				var extension = "";
				if (TextBasedSubtitles.Contains(mediaInfoSubtitleStream.CodecName))
				{
					Console.WriteLine("Text based.");
					extension = "srt";
				}
				else if (ImageBasedSubtitles.Contains(mediaInfoSubtitleStream.CodecName))
				{
					Console.WriteLine("Image Based.");
					extension = "vtt";
				}
				else
				{
					Console.WriteLine("Unsupported.");
					continue;
				}

				await ExtractMediaStream(mediaInfoSubtitleStream, path, extension);
			}

			for (var index = 0; index < mediaInfo.AudioStreams.Count; index++)
			{
				var mediaInfoAudioStream = mediaInfo.AudioStreams[index];
				Console.Write($"Audio {index}/{mediaInfo.SubtitleStreams.Count}. ");
				try
				{
					await ExtractMediaStream(mediaInfoAudioStream, path, mediaInfoAudioStream.CodecName);
				}
				catch (Exception e)
				{
					Console.WriteLine($"Failed to extract because: {e.Message}");
				}
			}
		}

		

		async Task ExtractMediaStream(MediaStream mediaStream, string path, string? extension)
		{
			var codecInfo = mediaStream.GetCodecInfo();
			var extMediaName = $"{Path.GetFileNameWithoutExtension(path)}";

			var mediaModifiers = RenderMediaStreamText(mediaStream, extension);
			Console.WriteLine(mediaModifiers);
			extMediaName += mediaModifiers;
			var directoryName = Path.GetDirectoryName(path);
			var targetFile = Path.Combine(directoryName, extMediaName);
			if (File.Exists(targetFile))
			{
				return;
			}

			var ffMpegArgumentProcessor = FFMpegArguments.FromFileInput(path, true)
				.OutputToFile(targetFile, false, f =>
				{
					f.SelectStream(mediaStream.Index);
					if (extension == null)
					{
						f.WithAudioCodec(codecInfo);
					}
					else if (extension is "vtt" or "srt")
					{
						f.WithVideoCodec(codecInfo);
					}
				})
				.NotifyOnProgress(d => { Console.WriteLine($"{d}/100%"); }, mediaStream.Duration);
			await ffMpegArgumentProcessor
				.ProcessAsynchronously(true);
		}
	}

	public static string RenderMediaStreamText(MediaStream mediaStream, string extension)
	{
		var mediaModifiers = "";
		if (mediaStream.Disposition?.TryGetValue("default", out var isDefault) == true && isDefault)
		{
			mediaModifiers += ".default";
		}

		if (mediaStream.Disposition?.TryGetValue("forced", out var isForced) == true && isForced)
		{
			mediaModifiers += ".forced";
		}

		if (mediaStream.Disposition?.TryGetValue("visual_impaired", out var isCC) == true && isCC)
		{
			mediaModifiers += ".cc";
		}

		if (mediaStream.Disposition?.TryGetValue("hearing_impaired", out var isSdh) == true && isSdh)
		{
			mediaModifiers += ".sdh";
		}

		if (!string.IsNullOrWhiteSpace(mediaStream.Language))
		{
			mediaModifiers += $".{mediaStream.Language}";
		}

		return mediaModifiers + $".{extension ?? mediaStream.CodecName}";
	}
}

public abstract class WorkerBase
{
	public string FfmpegPath { get; set; }

	public abstract Task Run();

	protected virtual string GetInput(string name)
	{
		return GetInputImpl(name);
	}

	public static string GetInputImpl(string name)
	{
		var sourceDir = Environment.GetEnvironmentVariable(name);
		if (!string.IsNullOrWhiteSpace(sourceDir))
		{
			Console.WriteLine($"[From Environment] {sourceDir}");
			return sourceDir;
		}
		else
		{
			var cmdArg = Environment.GetCommandLineArgs()
				.FirstOrDefault(e => e.StartsWith(name + "="));
			if (cmdArg != null)
			{
				var cmd = cmdArg[(name.Length + 1)..];
				Console.WriteLine($"[From Commandline] {cmd}");
				return cmd;
			}
			else
			{
				return Console.ReadLine();
			}
		}
	}
}