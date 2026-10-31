# Lyrics Sync for Arcanist

An official extension for [Arcanist](https://github.com/linkfast/arcanist), the self-hosted music server: it works
out the timing of plain lyrics on your server, from the song itself, so players can follow them line by line.

- **Only what has no timing.** Lyrics with their own timing (an `.lrc` file, LRC in the tags) and those LRCLib has
  (made by people) come first; with LRCLib on, only what it does not have is timed here.
- **On your server.** The song never leaves it. Arcanist's ffmpeg decodes it, [Spleeter](https://github.com/deezer/spleeter)
  separates the voice from the music, [Whisper](https://github.com/openai/whisper) (through
  [whisper.cpp](https://github.com/ggml-org/whisper.cpp)) recognizes the words with the moment each is sung, and they
  are matched to the lyrics as two sequences (misheard, missed and extra words do not shift the rest). Each line
  takes the moment of its first word; parts where whole lines went unheard are listened to again on their own. If
  less than half the lyrics match (another version, another language, a live take), nothing is saved.
- **In the background, at low priority.** One song at a time, waiting while Arcanist scans or someone listens; the
  songs a listener opens go first. Players get the timing through Arcanist's lyrics API (`syncedSource: ALIGNED`).
- **Re-sync on request.** A user who may edit tags (`EDIT_METADATA`) can ask for one song to be timed again
  (`resyncTrackLyrics`, `POST /rest/v1/tracks/{id}/lyrics/resync`; Arcanist 0.1.0-alpha.16 or newer), whatever timing
  it has (its file's, LRCLib's or one worked out here): it goes before everything else and is tried even if it was
  tried before. Its timing is replaced only when the words match the singing; otherwise it stays as it was.
- **Music videos** (Arcanist 0.1.0-alpha.16 or newer). A video that plays its song needs nothing: Arcanist carries
  the song's timing over to the stretches where the video plays it (`musicVideoLyrics`), so its scenes (an intro, a
  dialogue, an outro) get no lyrics, a verse it cuts is left out and a chorus it repeats shows again. A video with
  audio of its own (a live take, another edit, where the alignment found no match) is timed here on that audio, after
  the songs and first when someone watches it: what is said in its scenes is not in the lyrics, so the matching
  leaves it out.
- **Lyrics for songs that have none** (Arcanist 0.1.0-alpha.16 or newer). A player can ask for a song without lyrics
  anywhere (not in the file, not on LRCLib) to have its words recognized (`requestLyricsTranscription`); with
  *Recognize the lyrics of songs that have none* on (off by default), the extension also does it in the background,
  last of all. Whisper writes what is sung; what it makes up over instrumentals (credits, thanks, loops) and words
  where the separated voice is silent are left out, and it is cut into lines at pauses and sentence ends, each with
  the model's confidence. It is only a **draft**: nobody sees it as the song's lyrics until someone allowed to edit
  tags corrects and accepts it, in the panel's editor (*Review lyrics*, linked from the extension's page) or in a
  player's own.
- **Nothing on disk is rewritten.** The timing lives in Arcanist's database; a rescan keeps it while the words stay
  the same.

## Install

From Arcanist's panel: **Extensions › Marketplace › Lyrics Sync**, or **Settings › Timed lyrics › Install** when there
are lyrics to time. After the restart, the panel offers to download what it needs (checked against its SHA-256):

| | Size |
|---|---|
| Whisper small, 8-bit (or 5-bit for slow machines) | 264 MB (190 MB) |
| Spleeter 2-stems (voice and music), fp16 ONNX | 38 MB |
| whisper.cpp and ONNX Runtime for your machine | 3 + 10 MB |

It runs on 64-bit Windows, Linux (glibc 2.34 or newer: Debian 12, Ubuntu 22.04, Arcanist's Docker image) and macOS.
x64 processors without AVX2 get whisper.cpp's build without it.

**Speed.** On a 2014 Xeon (4 threads) a song takes about as long as it lasts; modern machines are several times faster
and a Raspberry Pi 5 several times slower. Separating the voice adds about a tenth.

## Settings

In **Extensions › Lyrics Sync**: time in the background (or only what listeners open), separate the voice, the
model, and the threads used for each song. The page shows how many lyrics are timed, what is left and what could
not be matched, and can try those again or delete the timing worked out here.

## Develop

```bash
dotnet test --project tests/Arcanist.LyricsSync.Tests   # needs Arcanist checked out at ../../Arcanist (or ARCANIST_ROOT)
sh pack.sh                                              # artifacts/arcanist-lyrics-sync-<version>.zip and its .sha256
```

The tests replace Whisper with transcripts read from files (`Arcanist:Extensions:lyrics-sync:Transcripts`).
`RealAudioTests` runs the real pipeline when `LYRICS_SYNC_REAL` names a folder with `song.*`, `lyrics.txt` and
`expected.json` (each line's start in ms); it downloads the components into that folder.

## License

AGPL-3.0-or-later, like Arcanist. Copyright (C) 2026 LinkFast S.A. and Arcanist contributors. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
