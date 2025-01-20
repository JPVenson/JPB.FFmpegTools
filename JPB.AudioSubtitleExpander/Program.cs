
using FFMpegCore;
using FFMpegCore.Enums;
using System;
using JPB.AudioSubtitleExpander;
using Polly;
Console.WriteLine("Ffmpeg and ffprobe path:");
var ffmpegPath = WorkerBase.GetInputImpl("ASE_FfmpegDirectory");


if (!Directory.Exists(ffmpegPath))
{
	Console.WriteLine("Directory does not exist.");
	return;
}
GlobalFFOptions.Configure(new FFOptions()
{
	BinaryFolder = ffmpegPath,
});

var modes = new Dictionary<string, WorkerBase>()
{
	{ "extract", new ExtractSubStreams() },
	{ "merge", new MergeSubStreams() },
	{ "measure", new MeasureSubStreams() },
	{ "copyfilter", new CopyWithSubStreams() },
	{ "deinterlace", new DeinterlaceStreams() },
};

Console.WriteLine("Operations:");
for (int i = 0; i < modes.Count; i++)
{
	var item = modes.ElementAt(i);
	Console.WriteLine($"\t - '{item.Key}'");
}

Console.Write("Select: ");
var mode = WorkerBase.GetInputImpl("ASE_Mode");
if (modes.TryGetValue(mode, out var worker))
{
	worker.FfmpegPath = ffmpegPath;
	await worker.Run();
}

Console.WriteLine("Done.");
Console.ReadLine();