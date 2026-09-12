using System.IO.Compression;
using System.Text;
using ShizuAppStoreServer.Core.Enrichment;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Launcher icons: binary AXML, resource table, drawable staging,
/// and service wiring. XML rendering itself runs LayoutLib via Paparazzi
/// (see tools/icon-render), so unit tests use a null or fake renderer.</summary>
public sealed class AdaptiveIconTests
{
    // Real res/BW.xml (548 bytes) and res/Qr.xml (1712 bytes) from CatShare.
    private const string BwXmlBase64 = "AwAIACQCAAABABwAtAAAAAcAAAAAAAAAAAEAADgAAAAAAAAAAAAAAAsAAAAbAAAAJQAAADIAAAA/AAAAbAAAAAgIZHJhd2FibGUADQ1hZGFwdGl2ZS1pY29uAAcHYW5kcm9pZAAKCmJhY2tncm91bmQACgpmb3JlZ3JvdW5kACoqaHR0cDovL3NjaGVtYXMuYW5kcm9pZC5jb20vYXBrL3Jlcy9hbmRyb2lkAAoKbW9ub2Nocm9tZQAAAACAAQgADAAAAJkBAQEAARAAGAAAAAIAAAD/////AgAAAAUAAAACARAAJAAAAAIAAAD//////////wEAAAAUABQAAAAAAAAAAAACARAAOAAAAAMAAAD//////////wMAAAAUABQAAQAAAAAAAAAFAAAAAAAAAP////8IAAABYAAFfwMBEAAYAAAAAwAAAP//////////AwAAAAIBEAA4AAAABAAAAP//////////BAAAABQAFAABAAAAAAAAAAUAAAAAAAAA/////wgAAAGVAAd/AwEQABgAAAAEAAAA//////////8EAAAAAgEQADgAAAAFAAAA//////////8GAAAAFAAUAAEAAAAAAAAABQAAAAAAAAD/////CAAAAZYAB38DARAAGAAAAAUAAAD//////////wYAAAADARAAGAAAAAIAAAD//////////wEAAAABARAAGAAAAAIAAAD/////AgAAAAUAAAA=";
    private const string QrXmlBase64 = "AwAIALAGAAABABwAUAQAABIAAAAAAAAAAAEAAGQAAAAAAAAAAAAAAAkAAAARAAAAGgAAACMAAAAzAAAARAAAAFAAAABbAAAAaQAAAHYAAACDAAAACgEAABQBAAAcAQAASQEAANsDAADiAwAABgZoZWlnaHQABQV3aWR0aAAGBnNjYWxlWAAGBnNjYWxlWQANDXZpZXdwb3J0V2lkdGgADg52aWV3cG9ydEhlaWdodAAJCWZpbGxDb2xvcgAICHBhdGhEYXRhAAsLc3Ryb2tlV2lkdGgACgp0cmFuc2xhdGVYAAoKdHJhbnNsYXRlWQCAgoCCTTMyMC4yMiwzNTEuNjhWMjM5LjEzaC0zMi4xMXYxMTIuNTVoLTQ4LjE3bDY0LjIyLDY0LjA2IDY0LjIyLC02NC4wNnpNMjA3LjgzLDEyNi43NSBMMTQzLjYxLDE5MC44MWg0OC4xN3YxMTIuNTVoMzIuMTFWMTkwLjgxaDQ4LjE3egAHB2FuZHJvaWQABQVncm91cAAqKmh0dHA6Ly9zY2hlbWFzLmFuZHJvaWQuY29tL2Fway9yZXMvYW5kcm9pZACCjYKNbTQ4MS44OCwyOTkuMzQgbDMwLjEzLC03LjEzIC00LjY2LC0xOS42NCAtMjQuMjUsNS43M2MwLC0wLjMzIDAuMDIsLTAuNjQgMC4wMiwtMC45NyAwLC0zOC4wOCAtMTAuNTMsLTcyLjc4IC0yOC45NywtMTAyLjM2IDE1LjQ0LC01My45NCAtMTMuNDQsLTEyNC41NSAtMTMuNDQsLTEyNC41NSAwLDAgLTUyLjg5LDIyLjU2IC04Mi45OCw0OS44NkMzMjcuMTEsOTEuMDYgMjkyLjU4LDg4LjA5IDI1Niw4OC4wOWMtMzYuNzcsMCAtNzEuNDgsMi41NiAtMTAyLjIsMTEuNzggLTMwLjE2LC0yNy4xMSAtODIuNSwtNDkuNDQgLTgyLjUsLTQ5LjQ0IDAsMCAtMjguODksNzAuNjMgLTEzLjQ0LDEyNC41NiAtMTguNDIsMjkuNTggLTI4Ljk4LDY0LjI4IC0yOC45OCwxMDIuMzQgMCwwLjMzIDAuMDMsMC42NCAwLjAzLDAuOTdMNC42NiwyNzIuNTggMCwyOTIuMjJsMzAuMTEsNy4xM2MyLjY3LDIzLjU4IDkuNjcsNDQuNzUgMjAuMjMsNjMuNDJsLTM3LjQ3LDE0Ljk4IDcuNDgsMTguNzUgNDEuMiwtMTYuNDhjMzkuOCw1My4xNCAxMTEuOTcsODEuNTUgMTk0LjQ0LDgxLjU1IDgyLjQ1LDAgMTU0LjY0LC0yOC40MSAxOTQuNDQsLTgxLjU1bDQxLjIsMTYuNDggNy40OCwtMTguNzUgLTM3LjQ3LC0xNC45OGMxMC41NiwtMTguNjcgMTcuNTYsLTM5Ljg0IDIwLjIyLC02My40MnoABARwYXRoAAYGdmVjdG9yAACAAQgANAAAAFUBAQFZAQEBJAMBASUDAQECBAEBAwQBAQQEAQEFBAEBBwQBAVoEAQFbBAEBAAEQABgAAAAFAAAA/////wwAAAAOAAAAAgEQAHQAAAAFAAAA//////////8RAAAAFAAUAAQAAAAAAAAADgAAAAAAAAD/////CAAABQFsAAAOAAAAAQAAAP////8IAAAFAWwAAA4AAAAEAAAA/////wgAAAQAAABEDgAAAAUAAAD/////CAAABAAAAEQCARAAdAAAAAkAAAD//////////w0AAAAUABQABAAAAAAAAAAOAAAAAgAAAP////8IAAAEzqsJPw4AAAADAAAA/////wgAAATOqwk/DgAAAAkAAAD/////CAAABGSo7EIOAAAACgAAAP////8IAAAEZKjsQgIBEABMAAAADAAAAP//////////EAAAABQAFAACAAAAAAAAAA4AAAAGAAAA/////wgAAB0AAAD/DgAAAAcAAAAPAAAACAAAAw8AAAADARAAGAAAAAwAAAD//////////xAAAAACARAAYAAAABAAAAD//////////xAAAAAUABQAAwAAAAAAAAAOAAAABgAAAP////8IAAAd/////w4AAAAHAAAACwAAAAgAAAMLAAAADgAAAAgAAAD/////CAAABM83J0MDARAAGAAAABAAAAD//////////xAAAAADARAAGAAAAAkAAAD//////////w0AAAADARAAGAAAAAUAAAD//////////xEAAAABARAAGAAAAAUAAAD/////DAAAAA4AAAA=";

    private static byte[] BwXml() => Convert.FromBase64String(BwXmlBase64);
    private static byte[] QrXml() => Convert.FromBase64String(QrXmlBase64);

    // Real AndroidManifest.xml from CatShare (icon is a compiled reference).
    private const string ManifestBase64 = "AwAIAGwzAAABABwAmBkAAHAAAAAAAAAAAAAAANwBAAAAAAAAAAAAAA4AAAAcAAAAKAAAADQAAABMAAAAbgAAAIAAAACUAAAAsAAAAMoAAAD0AAAAAgEAABYBAAAqAQAASAEAAGIBAAB8AQAAoAEAAL4BAADYAQAA7AEAAAYCAAAsAgAAUgIAAHQCAACKAgAAsAIAAOYCAAAQAwAAPgMAAGgDAACSAwAAnAMAAKYDAACuAwAAvgMAANIDAADkAwAAHAQAAFQEAACeBAAA4AQAACQFAACCBQAA2AUAACoGAAB2BgAA0AYAAAwHAABUBwAApAcAAPAHAAA2CAAAgggAALQIAAACCQAAcgkAANQJAAAyCgAAbAoAALgKAAAICwAAVgsAALQLAAAQDAAAWgwAAKoMAADyDAAATA0AAKwNAAAQDgAAfg4AAOoOAABODwAArA8AAAQQAAAoEAAAehAAAJQQAACoEAAAtBAAAAwRAAAqEQAAPhEAAFQRAAB8EQAA9hEAAEYSAACIEgAAzBIAABYTAABaEwAArhMAAPgTAAA6FAAAmBQAAPgUAABUFQAAthUAAO4VAAAsFgAAfBYAAI4WAACmFgAA2hYAAA4XAAAiFwAANhcAAHQXAACGFwAAqBcAAAUAdABoAGUAbQBlAAAABQBsAGEAYgBlAGwAAAAEAGkAYwBvAG4AAAAEAG4AYQBtAGUAAAAKAHAAZQByAG0AaQBzAHMAaQBvAG4AAAAPAHAAcgBvAHQAZQBjAHQAaQBvAG4ATABlAHYAZQBsAAAABwBlAG4AYQBiAGwAZQBkAAAACABlAHgAcABvAHIAdABlAGQAAAAMAG0AdQBsAHQAaQBwAHIAbwBjAGUAcwBzAAAACwBhAHUAdABoAG8AcgBpAHQAaQBlAHMAAAATAGcAcgBhAG4AdABVAHIAaQBQAGUAcgBtAGkAcwBzAGkAbwBuAHMAAAAFAHYAYQBsAHUAZQAAAAgAcgBlAHMAbwB1AHIAYwBlAAAACABtAGkAbQBlAFQAeQBwAGUAAAANAG0AaQBuAFMAZABrAFYAZQByAHMAaQBvAG4AAAALAHYAZQByAHMAaQBvAG4AQwBvAGQAZQAAAAsAdgBlAHIAcwBpAG8AbgBOAGEAbQBlAAAAEAB0AGEAcgBnAGUAdABTAGQAawBWAGUAcgBzAGkAbwBuAAAADQBtAGEAeABTAGQAawBWAGUAcgBzAGkAbwBuAAAACwBhAGwAbABvAHcAQgBhAGMAawB1AHAAAAAIAHIAZQBxAHUAaQByAGUAZAAAAAsAcwB1AHAAcABvAHIAdABzAFIAdABsAAAAEQBlAHgAdAByAGEAYwB0AE4AYQB0AGkAdgBlAEwAaQBiAHMAAAARAGYAdQBsAGwAQgBhAGMAawB1AHAAQwBvAG4AdABlAG4AdAAAAA8AZABpAHIAZQBjAHQAQgBvAG8AdABBAHcAYQByAGUAAAAJAHIAbwB1AG4AZABJAGMAbwBuAAAAEQBjAG8AbQBwAGkAbABlAFMAZABrAFYAZQByAHMAaQBvAG4AAAAZAGMAbwBtAHAAaQBsAGUAUwBkAGsAVgBlAHIAcwBpAG8AbgBDAG8AZABlAG4AYQBtAGUAAAATAGEAcABwAEMAbwBtAHAAbwBuAGUAbgB0AEYAYQBjAHQAbwByAHkAAAAVAGYAbwByAGUAZwByAG8AdQBuAGQAUwBlAHIAdgBpAGMAZQBUAHkAcABlAAAAEwBkAGEAdABhAEUAeAB0AHIAYQBjAHQAaQBvAG4AUgB1AGwAZQBzAAAAEwB1AHMAZQBzAFAAZQByAG0AaQBzAHMAaQBvAG4ARgBsAGEAZwBzAAAAAwAqAC8AKgAAAAMAMQAuADYAAAACADEANQAAAAYAYQBjAHQAaQBvAG4AAAAIAGEAYwB0AGkAdgBpAHQAeQAAAAcAYQBuAGQAcgBvAGkAZAAAABoAYQBuAGQAcgBvAGkAZAAuAGkAbgB0AGUAbgB0AC4AYQBjAHQAaQBvAG4ALgBNAEEASQBOAAAAGgBhAG4AZAByAG8AaQBkAC4AaQBuAHQAZQBuAHQALgBhAGMAdABpAG8AbgAuAFMARQBOAEQAAAAjAGEAbgBkAHIAbwBpAGQALgBpAG4AdABlAG4AdAAuAGEAYwB0AGkAbwBuAC4AUwBFAE4ARABfAE0AVQBMAFQASQBQAEwARQAAAB8AYQBuAGQAcgBvAGkAZAAuAGkAbgB0AGUAbgB0AC4AYwBhAHQAZQBnAG8AcgB5AC4ARABFAEYAQQBVAEwAVAAAACAAYQBuAGQAcgBvAGkAZAAuAGkAbgB0AGUAbgB0AC4AYwBhAHQAZQBnAG8AcgB5AC4ATABBAFUATgBDAEgARQBSAAAALQBhAG4AZAByAG8AaQBkAC4AcABlAHIAbQBpAHMAcwBpAG8AbgAuAEEAQwBDAEUAUwBTAF8AQgBBAEMASwBHAFIATwBVAE4ARABfAEwATwBDAEEAVABJAE8ATgAAACkAYQBuAGQAcgBvAGkAZAAuAHAAZQByAG0AaQBzAHMAaQBvAG4ALgBBAEMAQwBFAFMAUwBfAEMATwBBAFIAUwBFAF8ATABPAEMAQQBUAEkATwBOAAAAJwBhAG4AZAByAG8AaQBkAC4AcABlAHIAbQBpAHMAcwBpAG8AbgAuAEEAQwBDAEUAUwBTAF8ARgBJAE4ARQBfAEwATwBDAEEAVABJAE8ATgAAACQAYQBuAGQAcgBvAGkAZAAuAHAAZQByAG0AaQBzAHMAaQBvAG4ALgBBAEMAQwBFAFMAUwBfAFcASQBGAEkAXwBTAFQAQQBUAEUAAAArAGEAbgBkAHIAbwBpAGQALgBwAGUAcgBtAGkAcwBzAGkAbwBuAC4AQgBJAE4ARABfAFEAVQBJAEMASwBfAFMARQBUAFQASQBOAEcAUwBfAFQASQBMAEUAAAAcAGEAbgBkAHIAbwBpAGQALgBwAGUAcgBtAGkAcwBzAGkAbwBuAC4AQgBMAFUARQBUAE8ATwBUAEgAAAAiAGEAbgBkAHIAbwBpAGQALgBwAGUAcgBtAGkAcwBzAGkAbwBuAC4AQgBMAFUARQBUAE8ATwBUAEgAXwBBAEQATQBJAE4AAAAmAGEAbgBkAHIAbwBpAGQALgBwAGUAcgBtAGkAcwBzAGkAbwBuAC4AQgBMAFUARQBUAE8ATwBUAEgAXwBBAEQAVgBFAFIAVABJAFMARQAAACQAYQBuAGQAcgBvAGkAZAAuAHAAZQByAG0AaQBzAHMAaQBvAG4ALgBCAEwAVQBFAFQATwBPAFQASABfAEMATwBOAE4ARQBDAFQAAAAhAGEAbgBkAHIAbwBpAGQALgBwAGUAcgBtAGkAcwBzAGkAbwBuAC4AQgBMAFUARQBUAE8ATwBUAEgAXwBTAEMAQQBOAAAAJABhAG4AZAByAG8AaQBkAC4AcABlAHIAbQBpAHMAcwBpAG8AbgAuAEMASABBAE4ARwBFAF8AVwBJAEYASQBfAFMAVABBAFQARQAAABcAYQBuAGQAcgBvAGkAZAAuAHAAZQByAG0AaQBzAHMAaQBvAG4ALgBEAFUATQBQAAAAJQBhAG4AZAByAG8AaQBkAC4AcABlAHIAbQBpAHMAcwBpAG8AbgAuAEYATwBSAEUARwBSAE8AVQBOAEQAXwBTAEUAUgBWAEkAQwBFAAAANgBhAG4AZAByAG8AaQBkAC4AcABlAHIAbQBpAHMAcwBpAG8AbgAuAEYATwBSAEUARwBSAE8AVQBOAEQAXwBTAEUAUgBWAEkAQwBFAF8AQwBPAE4ATgBFAEMAVABFAEQAXwBEAEUAVgBJAEMARQAAAC8AYQBuAGQAcgBvAGkAZAAuAHAAZQByAG0AaQBzAHMAaQBvAG4ALgBGAE8AUgBFAEcAUgBPAFUATgBEAF8AUwBFAFIAVgBJAEMARQBfAEQAQQBUAEEAXwBTAFkATgBDAAAALQBhAG4AZAByAG8AaQBkAC4AcABlAHIAbQBpAHMAcwBpAG8AbgAuAEkATgBUAEUAUgBBAEMAVABfAEEAQwBSAE8AUwBTAF8AVQBTAEUAUgBTAF8ARgBVAEwATAAAABsAYQBuAGQAcgBvAGkAZAAuAHAAZQByAG0AaQBzAHMAaQBvAG4ALgBJAE4AVABFAFIATgBFAFQAAAAkAGEAbgBkAHIAbwBpAGQALgBwAGUAcgBtAGkAcwBzAGkAbwBuAC4ATABPAEMAQQBMAF8ATQBBAEMAXwBBAEQARABSAEUAUwBTAAAAJgBhAG4AZAByAG8AaQBkAC4AcABlAHIAbQBpAHMAcwBpAG8AbgAuAE4ARQBBAFIAQgBZAF8AVwBJAEYASQBfAEQARQBWAEkAQwBFAFMAAAAlAGEAbgBkAHIAbwBpAGQALgBwAGUAcgBtAGkAcwBzAGkAbwBuAC4AUABPAFMAVABfAE4ATwBUAEkARgBJAEMAQQBUAEkATwBOAFMAAAAtAGEAbgBkAHIAbwBpAGQALgBzAGUAcgB2AGkAYwBlAC4AcQB1AGkAYwBrAHMAZQB0AHQAaQBuAGcAcwAuAFQATwBHAEcATABFAEEAQgBMAEUAXwBUAEkATABFAAAALABhAG4AZAByAG8AaQBkAC4AcwBlAHIAdgBpAGMAZQAuAHEAdQBpAGMAawBzAGUAdAB0AGkAbgBnAHMALgBhAGMAdABpAG8AbgAuAFEAUwBfAFQASQBMAEUAAAAjAGEAbgBkAHIAbwBpAGQALgBzAHUAcABwAG8AcgB0AC4ARgBJAEwARQBfAFAAUgBPAFYASQBEAEUAUgBfAFAAQQBUAEgAUwAAACYAYQBuAGQAcgBvAGkAZAB4AC4AYwBvAHIAZQAuAGEAcABwAC4AQwBvAHIAZQBDAG8AbQBwAG8AbgBlAG4AdABGAGEAYwB0AG8AcgB5AAAAIgBhAG4AZAByAG8AaQBkAHgALgBjAG8AcgBlAC4AYwBvAG4AdABlAG4AdAAuAEYAaQBsAGUAUAByAG8AdgBpAGQAZQByAAAAKwBhAG4AZAByAG8AaQBkAHgALgBlAG0AbwBqAGkAMgAuAHQAZQB4AHQALgBFAG0AbwBqAGkAQwBvAG0AcABhAHQASQBuAGkAdABpAGEAbABpAHoAZQByAAAALgBhAG4AZAByAG8AaQBkAHgALgBsAGkAZgBlAGMAeQBjAGwAZQAuAFAAcgBvAGMAZQBzAHMATABpAGYAZQBjAHkAYwBsAGUASQBuAGkAdABpAGEAbABpAHoAZQByAAAAMABhAG4AZAByAG8AaQBkAHgALgBwAHIAbwBmAGkAbABlAGkAbgBzAHQAYQBsAGwAZQByAC4AUAByAG8AZgBpAGwAZQBJAG4AcwB0AGEAbABsAFIAZQBjAGUAaQB2AGUAcgAAADUAYQBuAGQAcgBvAGkAZAB4AC4AcAByAG8AZgBpAGwAZQBpAG4AcwB0AGEAbABsAGUAcgAuAFAAcgBvAGYAaQBsAGUASQBuAHMAdABhAGwAbABlAHIASQBuAGkAdABpAGEAbABpAHoAZQByAAAANABhAG4AZAByAG8AaQBkAHgALgBwAHIAbwBmAGkAbABlAGkAbgBzAHQAYQBsAGwAZQByAC4AYQBjAHQAaQBvAG4ALgBCAEUATgBDAEgATQBBAFIASwBfAE8AUABFAFIAQQBUAEkATwBOAAAAMABhAG4AZAByAG8AaQBkAHgALgBwAHIAbwBmAGkAbABlAGkAbgBzAHQAYQBsAGwAZQByAC4AYQBjAHQAaQBvAG4ALgBJAE4AUwBUAEEATABMAF8AUABSAE8ARgBJAEwARQAAAC0AYQBuAGQAcgBvAGkAZAB4AC4AcAByAG8AZgBpAGwAZQBpAG4AcwB0AGEAbABsAGUAcgAuAGEAYwB0AGkAbwBuAC4AUwBBAFYARQBfAFAAUgBPAEYASQBMAEUAAAAqAGEAbgBkAHIAbwBpAGQAeAAuAHAAcgBvAGYAaQBsAGUAaQBuAHMAdABhAGwAbABlAHIALgBhAGMAdABpAG8AbgAuAFMASwBJAFAAXwBGAEkATABFAAAAEABhAG4AZAByAG8AaQBkAHgALgBzAHQAYQByAHQAdQBwAAAAJwBhAG4AZAByAG8AaQBkAHgALgBzAHQAYQByAHQAdQBwAC4ASQBuAGkAdABpAGEAbABpAHoAYQB0AGkAbwBuAFAAcgBvAHYAaQBkAGUAcgAAAAsAYQBwAHAAbABpAGMAYQB0AGkAbwBuAAAACABjAGEAdABlAGcAbwByAHkAAAAEAGQAYQB0AGEAAAAqAGgAdAB0AHAAOgAvAC8AcwBjAGgAZQBtAGEAcwAuAGEAbgBkAHIAbwBpAGQALgBjAG8AbQAvAGEAcABrAC8AcgBlAHMALwBhAG4AZAByAG8AaQBkAAAADQBpAG4AdABlAG4AdAAtAGYAaQBsAHQAZQByAAAACABtAGEAbgBpAGYAZQBzAHQAAAAJAG0AZQB0AGEALQBkAGEAdABhAAAAEgBtAG8AZQAuAHIAZQBpAG0AdQAuAGMAYQB0AHMAaABhAHIAZQAAADsAbQBvAGUALgByAGUAaQBtAHUALgBjAGEAdABzAGgAYQByAGUALgBEAFkATgBBAE0ASQBDAF8AUgBFAEMARQBJAFYARQBSAF8ATgBPAFQAXwBFAFgAUABPAFIAVABFAEQAXwBQAEUAUgBNAEkAUwBTAEkATwBOAAAAJgBtAG8AZQAuAHIAZQBpAG0AdQAuAGMAYQB0AHMAaABhAHIAZQAuAEkATgBUAEUAUgBOAEEATABfAEIAUgBPAEEARABDAEEAUwBUAFMAAAAfAG0AbwBlAC4AcgBlAGkAbQB1AC4AYwBhAHQAcwBoAGEAcgBlAC4ATQBhAGkAbgBBAGMAdABpAHYAaQB0AHkAAAAgAG0AbwBlAC4AcgBlAGkAbQB1AC4AYwBhAHQAcwBoAGEAcgBlAC4ATQB5AEEAcABwAGwAaQBjAGEAdABpAG8AbgAAACMAbQBvAGUALgByAGUAaQBtAHUALgBjAGEAdABzAGgAYQByAGUALgBTAGUAdAB0AGkAbgBnAHMAQQBjAHQAaQB2AGkAdAB5AAAAIABtAG8AZQAuAHIAZQBpAG0AdQAuAGMAYQB0AHMAaABhAHIAZQAuAFMAaABhAHIAZQBBAGMAdABpAHYAaQB0AHkAAAAoAG0AbwBlAC4AcgBlAGkAbQB1AC4AYwBhAHQAcwBoAGEAcgBlAC4AUwB0AGEAcgB0AFIAZQBjAGUAaQB2AGUAcgBBAGMAdABpAHYAaQB0AHkAAAAjAG0AbwBlAC4AcgBlAGkAbQB1AC4AYwBhAHQAcwBoAGEAcgBlAC4AYQBuAGQAcgBvAGkAZAB4AC0AcwB0AGEAcgB0AHUAcAAAAB8AbQBvAGUALgByAGUAaQBtAHUALgBjAGEAdABzAGgAYQByAGUALgBmAGkAbABlAFAAcgBvAHYAaQBkAGUAcgAAAC0AbQBvAGUALgByAGUAaQBtAHUALgBjAGEAdABzAGgAYQByAGUALgBzAGUAcgB2AGkAYwBlAHMALgBHAGEAdAB0AFMAZQByAHYAZQByAFMAZQByAHYAaQBjAGUAAAAuAG0AbwBlAC4AcgBlAGkAbQB1AC4AYwBhAHQAcwBoAGEAcgBlAC4AcwBlAHIAdgBpAGMAZQBzAC4AUAAyAHAAUgBlAGMAZQBpAHYAZQByAFMAZQByAHYAaQBjAGUAAAAsAG0AbwBlAC4AcgBlAGkAbQB1AC4AYwBhAHQAcwBoAGEAcgBlAC4AcwBlAHIAdgBpAGMAZQBzAC4AUAAyAHAAUwBlAG4AZABlAHIAUwBlAHIAdgBpAGMAZQAAAC8AbQBvAGUALgByAGUAaQBtAHUALgBjAGEAdABzAGgAYQByAGUALgBzAGUAcgB2AGkAYwBlAHMALgBSAGUAYwBlAGkAdgBlAHIAVABpAGwAZQBTAGUAcgB2AGkAYwBlAAAAGgBtAG8AZQAuAHIAZQBpAG0AdQAuAGMAYQB0AHMAaABhAHIAZQAuAHMAaABpAHoAdQBrAHUAAAAdAG0AbwBlAC4AcwBoAGkAegB1AGsAdQAuAGMAbABpAGUAbgB0AC4AVgAzAF8AUwBVAFAAUABPAFIAVAAAACYAbQBvAGUALgBzAGgAaQB6AHUAawB1AC4AbQBhAG4AYQBnAGUAcgAuAHAAZQByAG0AaQBzAHMAaQBvAG4ALgBBAFAASQBfAFYAMgAzAAAABwBwAGEAYwBrAGEAZwBlAAAACgBwAGUAcgBtAGkAcwBzAGkAbwBuAAAAGABwAGwAYQB0AGYAbwByAG0AQgB1AGkAbABkAFYAZQByAHMAaQBvAG4AQwBvAGQAZQAAABgAcABsAGEAdABmAG8AcgBtAEIAdQBpAGwAZABWAGUAcgBzAGkAbwBuAE4AYQBtAGUAAAAIAHAAcgBvAHYAaQBkAGUAcgAAAAgAcgBlAGMAZQBpAHYAZQByAAAAHQByAGkAawBrAGEALgBzAGgAaQB6AHUAawB1AC4AUwBoAGkAegB1AGsAdQBQAHIAbwB2AGkAZABlAHIAAAAHAHMAZQByAHYAaQBjAGUAAAAPAHUAcwBlAHMALQBwAGUAcgBtAGkAcwBzAGkAbwBuAAAACAB1AHMAZQBzAC0AcwBkAGsAAACAAQgAiAAAAAAAAQEBAAEBAgABAQMAAQEGAAEBCQABAQ4AAQEQAAEBEwABARgAAQEbAAEBJAABASUAAQEmAAEBDAIBARsCAQEcAgEBcAIBAXECAQGAAgEBjgIBAa8DAQHqBAEB6wQBAQUFAQEsBQEBcgUBAXMFAQF6BQEBmQUBAT4GAQFEBgEBAAEQABgAAAACAAAA/////yUAAABRAAAAAgEQALAAAAACAAAA//////////9TAAAAFAAUAAcAAAAAAAAAUQAAAA8AAAD/////CAAAEAcAAABRAAAAEAAAACEAAAAIAAADIQAAAFEAAAAaAAAA/////wgAABAjAAAAUQAAABsAAAAiAAAACAAAAyIAAAD/////ZgAAAFUAAAAIAAADVQAAAP////9oAAAA/////wgAABAjAAAA/////2kAAAD/////CAAAEA8AAAACARAATAAAAAcAAAD//////////28AAAAUABQAAgAAAAAAAABRAAAADgAAAP////8IAAAQHQAAAFEAAAARAAAA/////wgAABAjAAAAAwEQABgAAAAHAAAA//////////9vAAAAAgEQAEwAAAALAAAA//////////9uAAAAFAAUAAIAAAAAAAAAUQAAAAMAAAA9AAAACAAAAz0AAABRAAAAHwAAAP////8IAAARAAABAAMBEAAYAAAACwAAAP//////////bgAAAAIBEABMAAAADgAAAP//////////bgAAABQAFAACAAAAAAAAAFEAAAADAAAALAAAAAgAAAMsAAAAUQAAABIAAAD/////CAAAECAAAAADARAAGAAAAA4AAAD//////////24AAAACARAATAAAABEAAAD//////////24AAAAUABQAAgAAAAAAAABRAAAAAwAAAC0AAAAIAAADLQAAAFEAAAASAAAA/////wgAABAgAAAAAwEQABgAAAARAAAA//////////9uAAAAAgEQAEwAAAAUAAAA//////////9uAAAAFAAUAAIAAAAAAAAAUQAAAAMAAAArAAAACAAAAysAAABRAAAAEgAAAP////8IAAAQIAAAAAMBEAAYAAAAFAAAAP//////////bgAAAAIBEABMAAAAFwAAAP//////////bgAAABQAFAACAAAAAAAAAFEAAAADAAAALgAAAAgAAAMuAAAAUQAAABQAAAD/////CAAAEv////8DARAAGAAAABcAAAD//////////24AAAACARAATAAAABoAAAD//////////24AAAAUABQAAgAAAAAAAABRAAAAAwAAADUAAAAIAAADNQAAAFEAAAAUAAAA/////wgAABL/////AwEQABgAAAAaAAAA//////////9uAAAAAgEQAEwAAAAdAAAA//////////9uAAAAFAAUAAIAAAAAAAAAUQAAAAMAAAA7AAAACAAAAzsAAABRAAAAFAAAAP////8IAAAS/////wMBEAAYAAAAHQAAAP//////////bgAAAAIBEABMAAAAIAAAAP//////////bgAAABQAFAACAAAAAAAAAFEAAAADAAAAMAAAAAgAAAMwAAAAUQAAABIAAAD/////CAAAEB4AAAADARAAGAAAACAAAAD//////////24AAAACARAATAAAACMAAAD//////////24AAAAUABQAAgAAAAAAAABRAAAAAwAAADEAAAAIAAADMQAAAFEAAAASAAAA/////wgAABAeAAAAAwEQABgAAAAjAAAA//////////9uAAAAAgEQAEwAAAAmAAAA//////////9uAAAAFAAUAAIAAAAAAAAAUQAAAAMAAAA0AAAACAAAAzQAAABRAAAAHwAAAP////8IAAARAAABAAMBEAAYAAAAJgAAAP//////////bgAAAAIBEAA4AAAAKQAAAP//////////bgAAABQAFAABAAAAAAAAAFEAAAADAAAAMgAAAAgAAAMyAAAAAwEQABgAAAApAAAA//////////9uAAAAAgEQADgAAAAqAAAA//////////9uAAAAFAAUAAEAAAAAAAAAUQAAAAMAAAAzAAAACAAAAzMAAAADARAAGAAAACoAAAD//////////24AAAACARAAOAAAACsAAAD//////////24AAAAUABQAAQAAAAAAAABRAAAAAwAAADcAAAAIAAADNwAAAAMBEAAYAAAAKwAAAP//////////bgAAAAIBEAA4AAAALAAAAP//////////bgAAABQAFAABAAAAAAAAAFEAAAADAAAAOAAAAAgAAAM4AAAAAwEQABgAAAAsAAAA//////////9uAAAAAgEQADgAAAAtAAAA//////////9uAAAAFAAUAAEAAAAAAAAAUQAAAAMAAAA5AAAACAAAAzkAAAADARAAGAAAAC0AAAD//////////24AAAACARAAOAAAAC4AAAD//////////24AAAAUABQAAQAAAAAAAABRAAAAAwAAAD4AAAAIAAADPgAAAAMBEAAYAAAALgAAAP//////////bgAAAAIBEAA4AAAALwAAAP//////////bgAAABQAFAABAAAAAAAAAFEAAAADAAAAPAAAAAgAAAM8AAAAAwEQABgAAAAvAAAA//////////9uAAAAAgEQAEwAAAAxAAAA//////////9nAAAAFAAUAAIAAAAAAAAAUQAAAAMAAABXAAAACAAAA1cAAABRAAAABQAAAP////8IAAARAgAAAAMBEAAYAAAAMQAAAP//////////ZwAAAAIBEAA4AAAANQAAAP//////////bgAAABQAFAABAAAAAAAAAFEAAAADAAAAVwAAAAgAAANXAAAAAwEQABgAAAA1AAAA//////////9uAAAAAgEQAEwAAAA3AAAA//////////9nAAAAFAAUAAIAAAAAAAAAUQAAAAMAAABWAAAACAAAA1YAAABRAAAABQAAAP////8IAAARAgAAAAMBEAAYAAAANwAAAP//////////ZwAAAAIBEAA4AAAAOwAAAP//////////bgAAABQAFAABAAAAAAAAAFEAAAADAAAAVgAAAAgAAANWAAAAAwEQABgAAAA7AAAA//////////9uAAAAAgEQADgAAAA8AAAA//////////9uAAAAFAAUAAEAAAAAAAAAUQAAAAMAAABlAAAACAAAA2UAAAADARAAGAAAADwAAAD//////////24AAAACARAAAAEAAD4AAAD//////////04AAAAUABQACwAAAAAAAABRAAAAAAAAAP////8IAAABJgIQf1EAAAABAAAA/////wgAAAEeAA9/UQAAAAIAAAD/////CAAAAQAADX9RAAAAAwAAAFkAAAAIAAADWQAAAFEAAAATAAAA/////wgAABL/////UQAAABUAAAD/////CAAAEv////9RAAAAFgAAAP////8IAAASAAAAAFEAAAAXAAAA/////wgAAAEAABJ/UQAAABkAAAD/////CAAAAQEADX9RAAAAHAAAAEIAAAAIAAADQgAAAFEAAAAeAAAA/////wgAAAEBABJ/AgEQAHQAAABKAAAA//////////8kAAAAFAAUAAQAAAAAAAAAUQAAAAAAAAD/////CAAAASYCEH9RAAAAAQAAAP////8IAAABJAEPf1EAAAADAAAAWgAAAAgAAANaAAAAUQAAAAcAAAD/////CAAAEgAAAAADARAAGAAAAEoAAAD//////////yQAAAACARAAdAAAAE8AAAD//////////yQAAAAUABQABAAAAAAAAABRAAAAAAAAAP////8IAAABJgIQf1EAAAABAAAA/////wgAAAEQAQ9/UQAAAAMAAABbAAAACAAAA1sAAABRAAAABwAAAP////8IAAAS/////wIBEAAkAAAAVAAAAP//////////UgAAABQAFAAAAAAAAAAAAAIBEAA4AAAAVQAAAP//////////IwAAABQAFAABAAAAAAAAAFEAAAADAAAAJwAAAAgAAAMnAAAAAwEQABgAAABVAAAA//////////8jAAAAAgEQADgAAABXAAAA//////////9PAAAAFAAUAAEAAAAAAAAAUQAAAAMAAAApAAAACAAAAykAAAADARAAGAAAAFcAAAD//////////08AAAACARAAOAAAAFkAAAD//////////1AAAAAUABQAAQAAAAAAAABRAAAADQAAACAAAAAIAAADIAAAAAMBEAAYAAAAWQAAAP//////////UAAAAAMBEAAYAAAAVAAAAP//////////UgAAAAIBEAAkAAAAWwAAAP//////////UgAAABQAFAAAAAAAAAAAAAIBEAA4AAAAXAAAAP//////////IwAAABQAFAABAAAAAAAAAFEAAAADAAAAKAAAAAgAAAMoAAAAAwEQABgAAABcAAAA//////////8jAAAAAgEQADgAAABeAAAA//////////9PAAAAFAAUAAEAAAAAAAAAUQAAAAMAAAApAAAACAAAAykAAAADARAAGAAAAF4AAAD//////////08AAAACARAAOAAAAGAAAAD//////////1AAAAAUABQAAQAAAAAAAABRAAAADQAAACAAAAAIAAADIAAAAAMBEAAYAAAAYAAAAP//////////UAAAAAMBEAAYAAAAWwAAAP//////////UgAAAAMBEAAYAAAATwAAAP//////////JAAAAAIBEABgAAAAYwAAAP//////////JAAAABQAFAADAAAAAAAAAFEAAAAAAAAA/////wgAAAF6AhB/UQAAAAMAAABcAAAACAAAA1wAAABRAAAABwAAAP////8IAAASAAAAAAMBEAAYAAAAYwAAAP//////////JAAAAAIBEACcAAAAaAAAAP//////////bQAAABQAFAAGAAAAAAAAAFEAAAABAAAA/////wgAAAH2AA9/UQAAAAIAAAD/////CAAAAYcAB39RAAAAAwAAAGIAAAAIAAADYgAAAFEAAAAEAAAALwAAAAgAAAMvAAAAUQAAAAYAAAD/////CAAAEv////9RAAAABwAAAP////8IAAAS/////wIBEABMAAAAbwAAAP//////////VAAAABQAFAACAAAAAAAAAFEAAAADAAAAPwAAAAgAAAM/AAAAUQAAAAsAAAD/////CAAAEv////8DARAAGAAAAG8AAAD//////////1QAAAACARAAJAAAAHMAAAD//////////1IAAAAUABQAAAAAAAAAAAACARAAOAAAAHQAAAD//////////yMAAAAUABQAAQAAAAAAAABRAAAAAwAAAEAAAAAIAAADQAAAAAMBEAAYAAAAdAAAAP//////////IwAAAAMBEAAYAAAAcwAAAP//////////UgAAAAMBEAAYAAAAaAAAAP//////////bQAAAAIBEAB0AAAAdwAAAP//////////bQAAABQAFAAEAAAAAAAAAFEAAAADAAAAYAAAAAgAAANgAAAAUQAAAAYAAAD/////CAAAEv////9RAAAABwAAAP////8IAAASAAAAAFEAAAAdAAAA/////wgAABEBAAAAAwEQABgAAAB3AAAA//////////9tAAAAAgEQAHQAAAB8AAAA//////////9tAAAAFAAUAAQAAAAAAAAAUQAAAAMAAABfAAAACAAAA18AAABRAAAABgAAAP////8IAAAS/////1EAAAAHAAAA/////wgAABIAAAAAUQAAAB0AAAD/////CAAAERAAAAADARAAGAAAAHwAAAD//////////20AAAACARAAdAAAAIEAAAD//////////20AAAAUABQABAAAAAAAAABRAAAAAwAAAGEAAAAIAAADYQAAAFEAAAAGAAAA/////wgAABL/////UQAAAAcAAAD/////CAAAEgAAAABRAAAAHQAAAP////8IAAARAQAAAAMBEAAYAAAAgQAAAP//////////bQAAAAIBEABgAAAAhwAAAP//////////JAAAABQAFAADAAAAAAAAAFEAAAAAAAAA/////wgAAAEmAhB/UQAAAAMAAABYAAAACAAAA1gAAABRAAAABwAAAP////8IAAAS/////wIBEAAkAAAAiwAAAP//////////UgAAABQAFAAAAAAAAAAAAAIBEAA4AAAAjAAAAP//////////IwAAABQAFAABAAAAAAAAAFEAAAADAAAAJgAAAAgAAAMmAAAAAwEQABgAAACMAAAA//////////8jAAAAAgEQADgAAACOAAAA//////////9PAAAAFAAUAAEAAAAAAAAAUQAAAAMAAAAqAAAACAAAAyoAAAADARAAGAAAAI4AAAD//////////08AAAADARAAGAAAAIsAAAD//////////1IAAAADARAAGAAAAIcAAAD//////////yQAAAACARAAnAAAAJIAAAD//////////2oAAAAUABQABgAAAAAAAABRAAAAAwAAAGwAAAAIAAADbAAAAFEAAAAEAAAAOgAAAAgAAAM6AAAAUQAAAAYAAAD/////CAAAEv////9RAAAABwAAAP////8IAAAS/////1EAAAAIAAAA/////wgAABIAAAAAUQAAAAkAAABjAAAACAAAA2MAAAADARAAGAAAAJIAAAD//////////2oAAAACARAAdAAAAJkAAAD//////////2oAAAAUABQABAAAAAAAAABRAAAAAwAAAEMAAAAIAAADQwAAAFEAAAAHAAAA/////wgAABIAAAAAUQAAAAkAAABeAAAACAAAA14AAABRAAAACgAAAP////8IAAAS/////wIBEABMAAAAngAAAP//////////VAAAABQAFAACAAAAAAAAAFEAAAADAAAAQQAAAAgAAANBAAAAUQAAAAwAAAD/////CAAAAQIAEn8DARAAGAAAAJ4AAAD//////////1QAAAADARAAGAAAAJkAAAD//////////2oAAAACARAAYAAAAKIAAAD//////////2oAAAAUABQAAwAAAAAAAABRAAAAAwAAAE0AAAAIAAADTQAAAFEAAAAHAAAA/////wgAABIAAAAAUQAAAAkAAABdAAAACAAAA10AAAACARAATAAAAKYAAAD//////////1QAAAAUABQAAgAAAAAAAABRAAAAAwAAAEQAAAAIAAADRAAAAFEAAAALAAAATAAAAAgAAANMAAAAAwEQABgAAACmAAAA//////////9UAAAAAgEQAEwAAACpAAAA//////////9UAAAAFAAUAAIAAAAAAAAAUQAAAAMAAABFAAAACAAAA0UAAABRAAAACwAAAEwAAAAIAAADTAAAAAMBEAAYAAAAqQAAAP//////////VAAAAAIBEABMAAAArAAAAP//////////VAAAABQAFAACAAAAAAAAAFEAAAADAAAARwAAAAgAAANHAAAAUQAAAAsAAABMAAAACAAAA0wAAAADARAAGAAAAKwAAAD//////////1QAAAADARAAGAAAAKIAAAD//////////2oAAAACARAATAAAALEAAAD//////////1QAAAAUABQAAgAAAAAAAABRAAAAAwAAAGQAAAAIAAADZAAAAFEAAAALAAAA/////wgAABL/////AwEQABgAAACxAAAA//////////9UAAAAAgEQAIgAAAC1AAAA//////////9rAAAAFAAUAAUAAAAAAAAAUQAAAAMAAABGAAAACAAAA0YAAABRAAAABAAAADYAAAAIAAADNgAAAFEAAAAGAAAA/////wgAABL/////UQAAAAcAAAD/////CAAAEv////9RAAAAGAAAAP////8IAAASAAAAAAIBEAAkAAAAuwAAAP//////////UgAAABQAFAAAAAAAAAAAAAIBEAA4AAAAvAAAAP//////////IwAAABQAFAABAAAAAAAAAFEAAAADAAAASQAAAAgAAANJAAAAAwEQABgAAAC8AAAA//////////8jAAAAAwEQABgAAAC7AAAA//////////9SAAAAAgEQACQAAAC+AAAA//////////9SAAAAFAAUAAAAAAAAAAAAAgEQADgAAAC/AAAA//////////8jAAAAFAAUAAEAAAAAAAAAUQAAAAMAAABLAAAACAAAA0sAAAADARAAGAAAAL8AAAD//////////yMAAAADARAAGAAAAL4AAAD//////////1IAAAACARAAJAAAAMEAAAD//////////1IAAAAUABQAAAAAAAAAAAACARAAOAAAAMIAAAD//////////yMAAAAUABQAAQAAAAAAAABRAAAAAwAAAEoAAAAIAAADSgAAAAMBEAAYAAAAwgAAAP//////////IwAAAAMBEAAYAAAAwQAAAP//////////UgAAAAIBEAAkAAAAxAAAAP//////////UgAAABQAFAAAAAAAAAAAAAIBEAA4AAAAxQAAAP//////////IwAAABQAFAABAAAAAAAAAFEAAAADAAAASAAAAAgAAANIAAAAAwEQABgAAADFAAAA//////////8jAAAAAwEQABgAAADEAAAA//////////9SAAAAAwEQABgAAAC1AAAA//////////9rAAAAAwEQABgAAAA+AAAA//////////9OAAAAAwEQABgAAAACAAAA//////////9TAAAAAQEQABgAAAACAAAA/////yUAAABRAAAA";

    private static byte[] ManifestXml() => Convert.FromBase64String(ManifestBase64);

    private static byte[] TestArsc() => SyntheticArsc.Build(
        ["res/Qr.xml", "res/lo.webp", "res/mid.xml"],
        [
            (0x05, 0x60, ResValueType.IntColorArgb8, 0xFF684C77, 0, 0),
            (0x07, 0x95, ResValueType.String, 0u, 0, 0),
            (0x07, 0x96, ResValueType.String, 1u, 160, 0),
            (0x07, 0x96, ResValueType.String, 2u, 640, 0),
        ]);

    [Fact]
    public void ParsesRealAdaptiveBinaryXml()
    {
        var root = BinaryXml.Parse(BwXml());

        Assert.NotNull(root);
        Assert.Equal("adaptive-icon", root.Name);
        var background = Assert.Single(root.Children, c => c.Name == "background");
        Assert.Equal("(0x7F050060)", background.Attributes["drawable"]);
        var foreground = Assert.Single(root.Children, c => c.Name == "foreground");
        Assert.Equal("(0x7F070095)", foreground.Attributes["drawable"]);
    }

    [Fact]
    public void ParsesRealVectorBinaryXml()
    {
        var root = BinaryXml.Parse(QrXml());

        Assert.NotNull(root);
        Assert.Equal("vector", root.Name);
        Assert.Equal(512f, float.Parse(root.Attributes["viewportWidth"],
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(512f, float.Parse(root.Attributes["viewportHeight"],
            System.Globalization.CultureInfo.InvariantCulture));
        var group = Assert.Single(root.Children, c => c.Name == "group");
        Assert.Equal(2, group.Children.Count(c => c.Name == "path"));
    }

    [Fact]
    public void ResolvesColorAndFileFromSyntheticArsc()
    {
        var arsc = TestArsc();

        Assert.Equal("#FF684C77", ApkResourceTable.ResolveString(arsc, 0x7F050060, allowXml: true));
        Assert.Equal("res/Qr.xml", ApkResourceTable.ResolveString(arsc, 0x7F070095, allowXml: true));
        // 0x96 pairs a density XML with a denser webp: every real device
        // picks the exact-density raster over another density's XML.
        Assert.Equal("res/lo.webp", ApkResourceTable.ResolveString(arsc, 0x7F070096, allowXml: true));
        Assert.Equal("res/lo.webp", ApkResourceTable.ResolveString(arsc, 0x7F070096, allowXml: false));
        Assert.Null(ApkResourceTable.ResolveString(arsc, 0x7F070099, allowXml: true));
    }

    [Fact]
    public void PrefersDensityRasterOverDefaultXml()
    {
        // Buge shape: a default-config vector next to density PNGs. Real
        // devices take the PNG; the vector is a dead fallback.
        var arsc = SyntheticArsc.Build(
            ["res/fallback.xml", "res/hi.png"],
            [
                (0x07, 0x90, ResValueType.String, 0u, 0, 0),
                (0x07, 0x90, ResValueType.String, 1u, 640, 0),
            ]);

        Assert.Equal("res/hi.png", ApkResourceTable.ResolveString(arsc, 0x7F070090, allowXml: true));
        // The manifest-icon path keeps its own XML-first order.
        Assert.Equal("res/fallback.xml", ApkResourceTable.ResolveXml(arsc, 0x7F070090));
        Assert.Equal("res/hi.png", ApkResourceTable.ResolveRaster(arsc, 0x7F070090));
    }

    [Fact]
    public void PrefersVersionedXmlOverDensityRaster()
    {
        // anydpi-v26 shape: the version qualifier outranks density on
        // modern devices, so the adaptive XML wins over the legacy PNG.
        var arsc = SyntheticArsc.Build(
            ["res/icon.xml", "res/icon.png"],
            [
                (0x07, 0x91, ResValueType.String, 0u, 0, 26),
                (0x07, 0x91, ResValueType.String, 1u, 640, 0),
            ]);

        Assert.Equal("res/icon.xml", ApkResourceTable.ResolveVersionedXml(arsc, 0x7F070091));
        Assert.Equal("res/icon.xml", ApkResourceTable.ResolveString(arsc, 0x7F070091, allowXml: true));
        Assert.Equal("res/icon.png", ApkResourceTable.ResolveString(arsc, 0x7F070091, allowXml: false));
    }

    [Fact]
    public void ResolvesFrameworkColorsWithoutAppEntries()
    {
        // Hyperbridge shape: fillColor="@android:color/white" with no app
        // entry at all. android.R ids are frozen public API, so the
        // embedded table inlines them; everything else stays unstageable.
        var arsc = SyntheticArsc.Build([], []);

        Assert.Equal("#FFFFFFFF", ApkResourceTable.ResolveString(arsc, 0x0106000B, allowXml: true));
        Assert.Equal("#FFFFFFFF", ApkResourceTable.ResolveString(arsc, 0x0106000B, allowXml: false));
        Assert.Equal("#FF2F3036", ApkResourceTable.ResolveString(arsc, 0x01060027, allowXml: false));
        // Colors are never XML, and unknown framework ids stay null.
        Assert.Null(ApkResourceTable.ResolveXml(arsc, 0x0106000B));
        Assert.Null(ApkResourceTable.ResolveString(arsc, 0x01070000, allowXml: true));
        Assert.Null(ApkResourceTable.ResolveString(arsc, 0x01020000, allowXml: true));
    }

    [Fact]
    public void ResolvesAppColorChainedToFrameworkColor()
    {
        // Tuner shape: an app color whose configs reference framework
        // colors (white default, dark on v31+). The modern-device tie-break
        // picks the versioned config, like a current phone would.
        var arsc = SyntheticArsc.Build(
            [],
            [
                (0x06, 0x75, ResValueType.Reference, 0x0106000B, 0, 0),
                (0x06, 0x75, ResValueType.Reference, 0x01060027, 0, 31),
            ]);

        Assert.Equal("#FF2F3036", ApkResourceTable.ResolveString(arsc, 0x7F060075, allowXml: true));
    }

    [Fact]
    public void PrefersHigherSdkOnDensityTie()
    {
        var arsc = SyntheticArsc.Build(
            ["res/old.xml", "res/new.xml"],
            [
                (0x07, 0x92, ResValueType.String, 0u, 0, 0),
                (0x07, 0x92, ResValueType.String, 1u, 0, 31),
            ]);

        Assert.Equal("res/new.xml", ApkResourceTable.ResolveString(arsc, 0x7F070092, allowXml: true));
    }

    [Fact]
    public void PrefersUnversionedAnyDpiXmlOverDensityRaster()
    {
        // HyperBridge shape: an unversioned anydpi adaptive XML next to
        // density webps. anydpi is density-independent by design, so the
        // XML wins; a default-config XML would still lose to the raster.
        var arsc = SyntheticArsc.Build(
            ["res/adaptive.xml", "res/icon.webp", "res/fallback.xml"],
            [
                (0x07, 0x93, ResValueType.String, 0u, 0xFFFE, 0),
                (0x07, 0x93, ResValueType.String, 1u, 640, 0),
                (0x07, 0x94, ResValueType.String, 2u, 0, 0),
                (0x07, 0x94, ResValueType.String, 1u, 640, 0),
            ]);

        Assert.Equal("res/adaptive.xml", ApkResourceTable.ResolveString(arsc, 0x7F070093, allowXml: true));
        Assert.Equal("res/icon.webp", ApkResourceTable.ResolveString(arsc, 0x7F070094, allowXml: true));
    }

    [Fact]
    public void StagesAdaptiveTreeToText()
    {
        var zipBytes = TestAssets.BuildApk(
            ("res/BW.xml", BwXml()),
            ("res/Qr.xml", QrXml()),
            ("resources.arsc", TestArsc()));
        var workDir = Path.Combine(Path.GetTempPath(), $"shizu-stagetest-{Guid.NewGuid():N}");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes));
            var staged = DrawableStager.Stage(zip, TestArsc(), "res/BW.xml", workDir);

            Assert.NotNull(staged);
            Assert.Equal("shizu_0", staged.DrawableName);
            // Adaptive roots stage beside res/: LayoutLib cannot inflate
            // them, and Paparazzi fails the render when it pre-parses one.
            var rootText = File.ReadAllText(Path.Combine(workDir, "shizu_0.xml"));
            Assert.Contains("adaptive-icon", rootText);
            Assert.Contains("#FF684C77", rootText); // background color inlined
            Assert.Contains("shizu_1", rootText); // foreground file staged alongside
            Assert.True(File.Exists(Path.Combine(staged.ResDir, "drawable", "shizu_1.xml")));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void StageReturnsNullForMissingFile()
    {
        var zipBytes = TestAssets.BuildApk(("res/BW.xml", BwXml()));
        var workDir = Path.Combine(Path.GetTempPath(), $"shizu-stagetest-{Guid.NewGuid():N}");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes));
            Assert.Null(DrawableStager.Stage(zip, TestArsc(), "res/nope.xml", workDir));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task XmlPathNeedsRenderer()
    {
        var zip = TestAssets.BuildApk(
            ("res/BW.xml", BwXml()),
            ("res/Qr.xml", QrXml()),
            ("resources.arsc", TestArsc()));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, zip);
            // Manifest-less path: badging names the adaptive XML directly, but
            // a null renderer disables the XML path entirely.
            var badging = BadgingParser.Parse(TestAssets.CannedBadging(iconPath: "res/BW.xml"));
            var icon = await new LauncherIconService().ResolveAsync(path, badging);

            Assert.Null(icon);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RendersStagedXmlThroughRenderer()
    {
        // Fake LayoutLib: 500x600 with a red marker inside the crop square
        // and a blue marker outside it (x=450 survives nothing past 432).
        using var marker = new Image<Rgba32>(500, 600, Color.White);
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                marker[x, y] = Color.Red; // inside the crop square
            }
        }

        for (var y = 100; y < 140; y++)
        {
            for (var x = 450; x < 490; x++)
            {
                marker[x, y] = Color.Blue; // outside it (x=450 survives nothing past 432)
            }
        }
        using var png = new MemoryStream();
        await marker.SaveAsPngAsync(png);
        var fake = new FakeRenderer(png.ToArray());

        var zip = TestAssets.BuildApk(
            ("res/BW.xml", BwXml()),
            ("res/Qr.xml", QrXml()),
            ("resources.arsc", TestArsc()));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, zip);
            var badging = BadgingParser.Parse(TestAssets.CannedBadging(iconPath: "res/BW.xml"));
            var icon = await new LauncherIconService(fake).ResolveAsync(path, badging);

            Assert.NotNull(icon);
            Assert.Equal("shizu_0", fake.SeenName); // root stages first
            Assert.True(icon.Adaptive); // BW.xml is an <adaptive-icon> root
            using var image = Image.Load<Rgba32>(icon.Png);
            Assert.Equal(192, image.Width);
            Assert.Equal(192, image.Height);
            var red = image[5, 5]; // inside the red marker after crop+shrink
            Assert.True(red.R > 200 && red.G < 100 && red.B < 100,
                $"expected red, got {red}");
            for (var y = 0; y < 192; y += 8)
            {
                for (var x = 0; x < 192; x += 8)
                {
                    var pixel = image[x, y];
                    Assert.False(pixel.B - pixel.R > 100, $"blue leak at {x},{y}: {pixel}");
                }
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PlainVectorRenderIsNotAdaptive()
    {
        // Curbox case: a plain <vector> root renders full-bleed through
        // Paparazzi but is NOT an adaptive icon (only <adaptive-icon>
        // roots count), so the flag stays false for squircle framing.
        using var white = new Image<Rgba32>(500, 500, Color.White);
        using var png = new MemoryStream();
        await white.SaveAsPngAsync(png);
        var fake = new FakeRenderer(png.ToArray());

        var zip = TestAssets.BuildApk(
            ("res/Qr.xml", QrXml()),
            ("resources.arsc", TestArsc()));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, zip);
            var badging = BadgingParser.Parse(TestAssets.CannedBadging(iconPath: "res/Qr.xml"));
            var icon = await new LauncherIconService(fake).ResolveAsync(path, badging);

            Assert.NotNull(icon);
            Assert.False(icon.Adaptive);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private sealed class FakeRenderer(byte[] png) : IPaparazziRenderer
    {
        public string? SeenName;
        public List<IReadOnlyList<BatchRenderRequest>> SeenBatches = [];

        public Task<byte[]> RenderAsync(string stagedResDir, string drawableName, int sizePx, CancellationToken ct = default)
        {
            Assert.True(Directory.Exists(stagedResDir)); // staged before render
            SeenName = drawableName;
            return Task.FromResult(png);
        }

        public Task<byte[]?[]> RenderBatchAsync(
            string stagedResDir, IReadOnlyList<BatchRenderRequest> batch, int sizePx, CancellationToken ct = default)
        {
            Assert.True(Directory.Exists(stagedResDir));
            SeenBatches.Add(batch);
            return Task.FromResult(batch.Select(_ => (byte[]?)png).ToArray());
        }
    }

    [Fact]
    public void DecodesComplexFractions()
    {
        // TitanPad inset: raw 322122544 must read as a 7.5% fraction.
        Assert.Equal(0.075f, ApkTypedValue.DecodeComplex(322122544), precision: 5);
        Assert.Equal("7.5%", ApkTypedValue.Convert([], ResValueType.Fraction, 322122544));
        Assert.Equal("108dp", ApkTypedValue.Convert([], ResValueType.Dimension, 0x6C01));
    }

    [Fact]
    public void NormalizationKeepsInsetWrapperWithPadding()
    {
        // Wadbs shape: the layer carries no drawable of its own, only
        // a nested inset. The wrapper stays intact (the renderer honors
        // its padding device-exactly); only the inner ref gets staged.
        var root = new XmlTreeNode("adaptive-icon");
        var bg = new XmlTreeNode("background");
        var inset = new XmlTreeNode("inset");
        inset.Attributes["drawable"] = "(0x7F070095)";
        inset.Attributes["inset"] = "24dp";
        bg.Children.Add(inset);
        root.Children.Add(bg);
        root.Children.Add(new XmlTreeNode("foreground"));

        DrawableStager.NormalizeAdaptiveRoot(root);

        Assert.False(bg.Attributes.ContainsKey("drawable"));
        var kept = Assert.Single(bg.Children);
        Assert.Equal("inset", kept.Name);
        Assert.Equal("(0x7F070095)", kept.Attributes["drawable"]);
        Assert.Equal("24dp", kept.Attributes["inset"]);
    }

    [Fact]
    public void NormalizationStillLiftsNonInsetWrappers()
    {
        // Exotic single-child wrappers (anything but inset) keep the old
        // lift: the renderer would otherwise resolve them to nothing.
        var root = new XmlTreeNode("adaptive-icon");
        var bg = new XmlTreeNode("background");
        var scale = new XmlTreeNode("scale");
        scale.Attributes["drawable"] = "(0x7F070095)";
        bg.Children.Add(scale);
        root.Children.Add(bg);

        DrawableStager.NormalizeAdaptiveRoot(root);

        Assert.Equal("(0x7F070095)", bg.Attributes["drawable"]);
        Assert.Empty(bg.Children);
    }

    [Fact]
    public void NormalizationKeepsDirectDrawableAndAddsWhiteBackground()
    {
        // KDE shape: foreground-only, direct ref. Missing bg defaults white.
        var root = new XmlTreeNode("adaptive-icon");
        var fg = new XmlTreeNode("foreground");
        fg.Attributes["drawable"] = "(0x7F070095)";
        root.Children.Add(fg);

        DrawableStager.NormalizeAdaptiveRoot(root);

        Assert.Equal("(0x7F070095)", fg.Attributes["drawable"]);
        var bg = Assert.Single(root.Children, c => c.Name == "background");
        Assert.Equal("#FFFFFFFF", bg.Attributes["drawable"]);
    }

    [Fact]
    public void NormalizationIgnoresNonAdaptiveRoots()
    {
        var root = new XmlTreeNode("vector");

        DrawableStager.NormalizeAdaptiveRoot(root);

        Assert.Empty(root.Children);
    }

    [Fact]
    public void ParsesRealManifestIconRef()
    {
        var root = BinaryXml.Parse(ManifestXml());

        Assert.NotNull(root);
        Assert.Equal("manifest", root.Name);
        var app = Assert.Single(root.Children, c => c.Name == "application");
        var iconRef = app.Attributes["icon"];
        Assert.StartsWith("(0x", iconRef);
        var iconId = uint.Parse(iconRef.Trim('(', ')').Replace("0x", ""),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0x7Fu, (iconId & 0xFF000000) >> 24); // app package
    }

    [Fact]
    public async Task ManifestXmlBeatsBadgingRasterWithRenderer()
    {
        // Mirror of the fallback test below, but with a renderer: the
        // manifest XML wins over the red badging raster (XML-first order).
        // The fake render is lime, so any red pixel proves the raster won.
        using var green = new Image<Rgba32>(432, 432, Color.Lime);
        using var png = new MemoryStream();
        await green.SaveAsPngAsync(png);
        var fake = new FakeRenderer(png.ToArray());

        var manifest = ManifestXml();
        var manifestRoot = BinaryXml.Parse(manifest);
        Assert.NotNull(manifestRoot);
        var app = Assert.Single(manifestRoot.Children, c => c.Name == "application");
        var iconId = uint.Parse(app.Attributes["icon"].Trim('(', ')').Replace("0x", ""),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        var iconType = (byte)((iconId >> 16) & 0xFF);
        var iconEntry = iconId & 0xFFFF;
        var arsc = SyntheticArsc.Build(
            ["res/BW.xml", "res/Qr.xml"],
            [
                (iconType, iconEntry, ResValueType.String, 0u, 0, 0),
                (0x05, 0x60, ResValueType.IntColorArgb8, 0xFF684C77, 0, 0),
                (0x07, 0x95, ResValueType.String, 1u, 0, 0),
                (0x07, 0x96, ResValueType.String, 1u, 0, 0), // monochrome layer
            ]);
        var zip = TestAssets.BuildApk(
            ("AndroidManifest.xml", manifest),
            ("resources.arsc", arsc),
            ("res/BW.xml", BwXml()),
            ("res/Qr.xml", QrXml()),
            ("res/icon.png", TestAssets.SolidPng(256, 256, Color.Red)));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, zip);
            var badging = BadgingParser.Parse(TestAssets.CannedBadging(iconPath: "res/icon.png"));
            var icon = await new LauncherIconService(fake).ResolveAsync(path, badging);

            Assert.NotNull(icon);
            Assert.Equal("shizu_0", fake.SeenName); // manifest XML staged, not the raster
            using var image = Image.Load<Rgba32>(icon.Png);
            var center = image[96, 96];
            Assert.True(center.G > 200 && center.R < 100, $"expected green, got {center}");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void StagePrefixesKeepSharedBatchDirCollisionFree()
    {
        // Two apps stage the same root into one shared dir (the batch
        // flow); unique prefixes must keep every file distinct.
        var workDir = Path.Combine(Path.GetTempPath(), $"shizu-stagetest-{Guid.NewGuid():N}");
        try
        {
            DrawableStager.StagedDrawable? first, second;
            using (var zip = new ZipArchive(new MemoryStream(TestAssets.BuildApk(("res/Qr.xml", QrXml())))))
            {
                first = DrawableStager.Stage(zip, TestArsc(), "res/Qr.xml", workDir, "b1");
            }

            using (var zip = new ZipArchive(new MemoryStream(TestAssets.BuildApk(("res/Qr.xml", QrXml())))))
            {
                second = DrawableStager.Stage(zip, TestArsc(), "res/Qr.xml", workDir, "b2");
            }

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal("b1_0", first.DrawableName);
            Assert.Equal("b2_0", second.DrawableName);
            Assert.True(File.Exists(Path.Combine(workDir, "res", "drawable", "b1_0.xml")));
            Assert.True(File.Exists(Path.Combine(workDir, "res", "drawable", "b2_0.xml")));
            Assert.Null(first.RootFile); // plain vector: no adaptive root beside res/
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void PrepareBatchPrefersManifestRasterOverBadgingXml()
    {
        // Manifest icon id resolves to a raster PNG while badging also
        // names an XML: single mode takes the manifest raster without ever
        // trying the badging XML, so batch must report "no XML" too (or
        // icons would flip between single and batch runs).
        var manifest = ManifestXml();
        var manifestRoot = BinaryXml.Parse(manifest);
        Assert.NotNull(manifestRoot);
        var app = Assert.Single(manifestRoot.Children, c => c.Name == "application");
        var iconId = uint.Parse(app.Attributes["icon"].Trim('(', ')').Replace("0x", ""),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        var iconType = (byte)((iconId >> 16) & 0xFF);
        var iconEntry = iconId & 0xFFFF;
        var arsc = SyntheticArsc.Build(
            ["res/icon.png"],
            [(iconType, iconEntry, ResValueType.String, 0u, 0, 0)]);
        var zipBytes = TestAssets.BuildApk(
            ("AndroidManifest.xml", manifest),
            ("resources.arsc", arsc),
            ("res/BW.xml", BwXml()),
            ("res/icon.png", TestAssets.SolidPng(256, 256, Color.Red)));
        var workDir = Path.Combine(Path.GetTempPath(), $"shizu-stagetest-{Guid.NewGuid():N}");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes));
            var badging = BadgingParser.Parse(TestAssets.CannedBadging(iconPath: "res/BW.xml"));
            var pending = new LauncherIconService(new FakeRenderer([]))
                .PrepareBatchRender(zip, arsc, badging, workDir, "b9");

            Assert.Null(pending);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ManifestXmlFallsBackToBadgingRasterWithoutRenderer()
    {
        // Same icon id resolves to an adaptive XML while badging also
        // names a real raster PNG: without a renderer the XML is skipped
        // and the badging raster wins over an avatar.
        var manifest = ManifestXml();
        var manifestRoot = BinaryXml.Parse(manifest);
        Assert.NotNull(manifestRoot);
        var app = Assert.Single(manifestRoot.Children, c => c.Name == "application");
        var iconId = uint.Parse(app.Attributes["icon"].Trim('(', ')').Replace("0x", ""),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        var iconType = (byte)((iconId >> 16) & 0xFF);
        var iconEntry = iconId & 0xFFFF;
        var arsc = SyntheticArsc.Build(
            ["res/BW.xml", "res/Qr.xml"],
            [
                (iconType, iconEntry, ResValueType.String, 0u, 0, 0),
                (0x05, 0x60, ResValueType.IntColorArgb8, 0xFF684C77, 0, 0),
                (0x07, 0x95, ResValueType.String, 1u, 0, 0),
            ]);
        var zip = TestAssets.BuildApk(
            ("AndroidManifest.xml", manifest),
            ("resources.arsc", arsc),
            ("res/BW.xml", BwXml()),
            ("res/Qr.xml", QrXml()),
            ("res/icon.png", TestAssets.SolidPng(256, 256, Color.Red)));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, zip);
            var badging = BadgingParser.Parse(TestAssets.CannedBadging(iconPath: "res/icon.png"));
            var icon = await new LauncherIconService().ResolveAsync(path, badging);

            Assert.NotNull(icon);
            using var image = Image.Load<Rgba32>(icon.Png);
            Assert.Equal(Color.Red.ToPixel<Rgba32>(), image[5, 5]);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ExtractsBadgingListedWebp()
    {
        // Proves ImageSharp decodes WebP: badging names the file directly.
        var webp = TestAssets.SolidWebp(256, 256, Color.Teal);
        var zip = TestAssets.BuildApk(("res/icon.webp", webp));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, zip);
            var badging = BadgingParser.Parse(TestAssets.CannedBadging(iconPath: "res/icon.webp"));

            var icon = IconProcessor.ExtractBestIcon(path, badging);

            Assert.Equal(IconProcessor.ProcessRawImage(webp)!.Sha256, icon!.Sha256);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task LiveRendersRealApksWhenProvided()
    {
        // Needs the Gradle tool checkout plus real APKs, e.g.
        // SHIZU_ICON_TOOL=/tmp/opencode/iconrender
        // SHIZU_REAL_APKS=/tmp/opencode with catshare.apk + autodnd.apk
        // (RealAapt2 precedent).
        var toolDir = Environment.GetEnvironmentVariable("SHIZU_ICON_TOOL");
        var dir = Environment.GetEnvironmentVariable("SHIZU_REAL_APKS");
        if (string.IsNullOrEmpty(toolDir) || string.IsNullOrEmpty(dir))
        {
            return; // Not a failure: env-gated by design.
        }

        var gradle = Environment.GetEnvironmentVariable("SHIZU_GRADLE") ?? "gradle";
        var service = new LauncherIconService(
            new PaparazziRenderer(gradle, toolDir, TimeSpan.FromMinutes(15)));
        foreach (var name in new[] { "catshare.apk", "autodnd.apk" })
        {
            var apk = Path.Combine(dir, name);
            if (!File.Exists(apk))
            {
                continue;
            }

            var badging = BadgingParser.Parse(RealBadging(apk));
            var icon = await service.ResolveAsync(apk, badging);

            Assert.NotNull(icon);
            using var image = Image.Load<Rgba32>(icon.Png);
            Assert.Equal(192, image.Width);
            var distinct = new HashSet<Rgba32>();
            for (var y = 0; y < 192; y += 8)
            {
                for (var x = 0; x < 192; x += 8)
                {
                    distinct.Add(image[x, y]);
                }
            }

            Assert.True(distinct.Count > 3, $"{name}: only {distinct.Count} sampled colors");
        }
    }

    private static string RealBadging(string apk)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("SHIZU_REAL_AAPT2") ?? "aapt2",
            $"dump badging \"{apk}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    /// <summary>Minimal resources.arsc emitter (string pool + package + typed entries).</summary>
    internal static class SyntheticArsc
    {
        public static byte[] Build(
            string[] strings,
            (byte TypeId, uint EntryId, ResValueType DataType, uint Data, ushort Density, ushort Sdk)[] entries)
        {
            var pool = BuildPool(strings);
            using var table = new MemoryStream();
            WriteU16(table, 0x0002); // RES_TABLE_TYPE
            WriteU16(table, 12);
            var tableSizePos = (int)table.Position;
            WriteU32(table, 0); // size, patched later
            WriteU32(table, 1); // packageCount
            table.Write(pool, 0, pool.Length);

            // Package chunk: id 0x7f, then padding to the 268-byte header.
            using var pack = new MemoryStream();
            WriteU16(pack, 0x0200);
            WriteU16(pack, 268);
            var packSizePos = (int)pack.Position;
            WriteU32(pack, 0);
            WriteU32(pack, 0x7f);
            pack.Write(new byte[256], 0, 256);
            var emptyPool = BuildPool([]);
            pack.Write(emptyPool, 0, emptyPool.Length); // type strings
            pack.Write(emptyPool, 0, emptyPool.Length); // key strings

            foreach (var group in entries.GroupBy(e => (e.TypeId, e.Density, e.Sdk)).OrderBy(g => g.Key))
            {
                var members = group.OrderBy(e => e.EntryId).ToList();
                var maxEntry = (int)members.Max(e => e.EntryId);
                using var chunk = new MemoryStream();
                WriteU16(chunk, 0x0201); // RES_TABLE_TYPE_TYPE
                WriteU16(chunk, 52);
                var chunkSizePos = (int)chunk.Position;
                WriteU32(chunk, 0);
                chunk.WriteByte(group.Key.TypeId);
                chunk.WriteByte(0);
                WriteU16(chunk, 0);
                WriteU32(chunk, (uint)(maxEntry + 1)); // entryCount
                // Index lives at 52; entry blobs follow it, so entriesStart
                // accounts for the index size (sparse types rely on this split).
                var entriesStart = 52 + 4 * (maxEntry + 1);
                WriteU32(chunk, (uint)entriesStart); // entriesStart
                WriteU32(chunk, 32); // config size
                var config = new byte[28];
                config[10] = (byte)(group.Key.Density & 0xFF);
                config[11] = (byte)((group.Key.Density >> 8) & 0xFF);
                config[20] = (byte)(group.Key.Sdk & 0xFF);
                config[21] = (byte)((group.Key.Sdk >> 8) & 0xFF);
                chunk.Write(config, 0, config.Length);

                var index = new uint[maxEntry + 1];
                var blobs = new List<byte[]>();
                var nextOffset = 0u;
                for (var i = 0; i <= maxEntry; i++)
                {
                    index[i] = 0xFFFFFFFF;
                }

                foreach (var e in members)
                {
                    using var entry = new MemoryStream();
                    WriteU16(entry, 16); // size
                    WriteU16(entry, 0); // flags
                    WriteU32(entry, 0); // key
                    WriteU16(entry, 8); // value size
                    entry.WriteByte(0); // res0
                    entry.WriteByte((byte)e.DataType);
                    WriteU32(entry, e.Data);
                    index[e.EntryId] = nextOffset;
                    var blob = entry.ToArray();
                    blobs.Add(blob);
                    nextOffset += (uint)blob.Length;
                }

                foreach (var slot in index)
                {
                    WriteU32(chunk, slot);
                }

                foreach (var blob in blobs)
                {
                    chunk.Write(blob, 0, blob.Length);
                }

                var chunkBytes = chunk.ToArray();
                BitConverter.GetBytes((uint)chunkBytes.Length).CopyTo(chunkBytes, chunkSizePos);
                pack.Write(chunkBytes, 0, chunkBytes.Length);
            }

            var packBytes = pack.ToArray();
            BitConverter.GetBytes((uint)packBytes.Length).CopyTo(packBytes, packSizePos);
            table.Write(packBytes, 0, packBytes.Length);
            var tableBytes = table.ToArray();
            BitConverter.GetBytes((uint)tableBytes.Length).CopyTo(tableBytes, tableSizePos);
            return tableBytes;
        }

        private static byte[] BuildPool(string[] strings)
        {
            using var ms = new MemoryStream();
            WriteU16(ms, 0x0001); // RES_STRING_POOL_TYPE
            WriteU16(ms, 28);
            var sizePos = (int)ms.Position;
            WriteU32(ms, 0);
            WriteU32(ms, (uint)strings.Length);
            WriteU32(ms, 0); // styleCount
            WriteU32(ms, 0); // flags: UTF-16
            WriteU32(ms, (uint)(28 + 4 * strings.Length)); // stringsStart
            WriteU32(ms, 0); // stylesStart
            var offset = 0;
            var blobs = new List<byte[]>();
            foreach (var s in strings)
            {
                WriteU32(ms, (uint)offset);
                var encoded = Encoding.Unicode.GetBytes(s);
                using var entry = new MemoryStream();
                WriteU16(entry, (ushort)s.Length);
                entry.Write(encoded, 0, encoded.Length);
                blobs.Add(entry.ToArray());
                offset += 2 + encoded.Length;
            }

            foreach (var blob in blobs)
            {
                ms.Write(blob, 0, blob.Length);
            }

            var bytes = ms.ToArray();
            BitConverter.GetBytes((uint)bytes.Length).CopyTo(bytes, sizePos);
            return bytes;
        }

        private static void WriteU16(Stream stream, ushort value) =>
            stream.Write(BitConverter.GetBytes(value), 0, 2);

        private static void WriteU32(Stream stream, uint value) =>
            stream.Write(BitConverter.GetBytes(value), 0, 4);
    }
}
