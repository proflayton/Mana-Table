'use strict';

// Original synthesized notification; no sampled or third-party audio.
// Regenerate with: node scripts/mana-table/generate-priority-cue.cjs
const fs = require('node:fs');
const path = require('node:path');
const sampleRate = 44100;
const samples = new Float64Array(Math.round(sampleRate * 0.4));
for (let index = 0; index < samples.length; index++) {
  const time = index / sampleRate;
  for (const [start, frequency] of [[0, 523.25], [0.1, 659.25]]) {
    const age = time - start;
    if (age < 0 || age >= 0.26) continue;
    const attack = Math.min(1, age / 0.02);
    const release = Math.min(1, (0.26 - age) / 0.07);
    const envelope = attack * release * Math.exp(-age * 10);
    samples[index] += envelope * (Math.sin(age * frequency * Math.PI * 2)
      + 0.08 * Math.sin(age * frequency * Math.PI * 4));
  }
}
const peak = samples.reduce((maximum, sample) => Math.max(maximum, Math.abs(sample)), 0);
const wav = Buffer.alloc(44 + samples.length * 2);
wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8);
wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
wav.writeUInt32LE(sampleRate, 24); wav.writeUInt32LE(sampleRate * 2, 28);
wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36);
wav.writeUInt32LE(samples.length * 2, 40);
for (let index = 0; index < samples.length; index++)
  wav.writeInt16LE(Math.round(samples[index] / peak * 0.24 * 32767), 44 + index * 2);
const output = path.resolve(__dirname, '../../mana-native/Mana.Game/Assets/your-priority.wav');
fs.writeFileSync(output, wav);
console.log(`Wrote ${path.basename(output)}: ${samples.length / sampleRate}s, mono PCM16, ${wav.length} bytes.`);
