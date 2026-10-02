# Sound preview

Item: P3. Data from today's `SoundDetails` and `WaveformView`; playback via the existing commands.

## Layout

- Header actions: **Save as WAV…**, **Replace from WAV…** (no Edit: sounds are replaced, not edited).
- Transport row: 40 px round primary button (play triangle / stop square, `aria-label` Play / Stop) · time in mono 14 "0:00.62" + muted "/ 0:01.48" · hint "Space plays and stops · stops when the selection changes" · right: check box "Repeat the loop".
- Detail chips (1 px CmCardBorder, radius 6, label in CmTextMuted): Rate `22,254.545 Hz` · Channels mono/stereo · Sample 8-bit · Length `1.48 s · 32,937 frames` · Loop `4,000–32,000` · Format "sampled, uncompressed". Same facts as today's details line, split.
- Waveform card: header strip "Channel 1" + legend swatch "Loop"; one 170 px lane per channel; centre line CmDivider; waveform fill CmAccent; loop range as a CmLoopRegion band behind it; playhead 2 px CmPlayhead. Time ruler under it in mono 11 (0.00 s … end).
- Click on the waveform starts playback from that point.

The playhead, click-to-seek and Repeat the loop need playback position, seek and loop support in `IAudioPlayer` / `SoundFlowPlayer` (item A1). Until A1 lands, show the waveform and loop band without a playhead and leave the repeat check box out.

## Error state

When a sound can't be decoded (e.g. `sound.unknown-format`): a card on CmSidebarBackground with the error icon, "No preview for `'snd ' 8192`", "Unknown sound format 3 · `sound.unknown-format`", and buttons **Show in Hex** and **Save raw data…**.
