import { crc32, inflateSync } from 'node:zlib';

export function validatePng(bytes, frame) {
  if (bytes.length < 45 || bytes.length > 16 * 1024 * 1024
    || bytes.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a'
    || bytes.readUInt32BE(8) !== 13 || bytes.subarray(12, 16).toString('latin1') !== 'IHDR'
    || bytes.readUInt32BE(16) !== frame.width || bytes.readUInt32BE(20) !== frame.height)
    throw new Error('PNG does not match the recorded frame dimensions');
  const width = bytes.readUInt32BE(16), height = bytes.readUInt32BE(20);
  const bitDepth = bytes[24], colorType = bytes[25];
  if (!width || !height || width * height > 16000000 || bitDepth !== 8
    || ![2, 6].includes(colorType) || bytes[26] !== 0 || bytes[27] !== 0 || bytes[28] !== 0)
    throw new Error('Unsupported or invalid PNG image format; expected bounded 8-bit RGB/RGBA, noninterlaced');
  let offset = 8, sawData = false, dataClosed = false, chunks = 0;
  const imageData = [];
  while (offset + 12 <= bytes.length && chunks++ < 4096) {
    const length = bytes.readUInt32BE(offset), end = offset + length + 12;
    if (end > bytes.length) throw new Error('Truncated PNG chunk');
    const type = bytes.subarray(offset + 4, offset + 8).toString('latin1');
    if (!/^[A-Za-z]{4}$/.test(type) || crc32(bytes.subarray(offset + 4, end - 4)) !== bytes.readUInt32BE(end - 4))
      throw new Error('PNG chunk checksum failed');
    if (type === 'IHDR' && offset !== 8) throw new Error('Duplicate PNG header');
    if (type[0] === type[0].toUpperCase() && !['IHDR', 'PLTE', 'IDAT', 'IEND'].includes(type))
      throw new Error('Unsupported critical PNG chunk');
    if (type === 'IDAT') {
      if (dataClosed) throw new Error('Noncontiguous PNG image data');
      sawData = true;
      imageData.push(bytes.subarray(offset + 8, end - 4));
    } else if (sawData) dataClosed = true;
    if (type === 'IEND') {
      if (!sawData || length !== 0 || end !== bytes.length) throw new Error('Invalid PNG ending');
      const rowBytes = width * (colorType === 6 ? 4 : 3) + 1;
      const expected = rowBytes * height;
      let pixels;
      try { pixels = inflateSync(Buffer.concat(imageData), { maxOutputLength: expected + 1 }); }
      catch { throw new Error('PNG image data is invalid or exceeds its declared geometry'); }
      if (pixels.length !== expected) throw new Error('PNG scanline length differs from its declared geometry');
      for (let row = 0; row < height; row++)
        if (pixels[row * rowBytes] > 4) throw new Error('Invalid PNG scanline filter');
      return;
    }
    offset = end;
  }
  throw new Error('PNG has no complete ending');
}
