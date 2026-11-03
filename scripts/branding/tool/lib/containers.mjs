// Icon and archive containers the generator writes around PNGs it rendered: ICO (Windows), ICNS (macOS) and a ZIP
// (the brand kit). Each is written byte for byte from its inputs, with nothing taken from the clock or the machine,
// so the same PNGs give the same file.
import zlib from 'node:zlib';

// An ICO with PNG entries (supported since Windows Vista and by every browser). An entry of 256 px or more writes 0
// as its width and height, as the format says.
export function ico(pngs) {
  const header = Buffer.alloc(6 + 16 * pngs.length);
  header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(pngs.length, 4);
  let offset = header.length;
  pngs.forEach(({ size, data }, i) => {
    const e = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, e); header.writeUInt8(size >= 256 ? 0 : size, e + 1);
    header.writeUInt8(0, e + 2); header.writeUInt8(0, e + 3);
    header.writeUInt16LE(1, e + 4); header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(data.length, e + 8); header.writeUInt32LE(offset, e + 12);
    offset += data.length;
  });
  return Buffer.concat([header, ...pngs.map((p) => p.data)]);
}

// The ICNS element types that hold a PNG, by pixel size. A size of two types (32: 32 px and 16 px @2x) is written
// under both, so a Retina screen finds its entry too.
const icnsTypes = {
  16: ['icp4'], 32: ['icp5', 'ic11'], 64: ['icp6', 'ic12'], 128: ['ic07'],
  256: ['ic08', 'ic13'], 512: ['ic09', 'ic14'], 1024: ['ic10'],
};
export const icnsSizes = Object.keys(icnsTypes).map(Number);

export function icns(pngs) {
  const elements = [];
  for (const { size, data } of pngs) {
    const types = icnsTypes[size];
    if (!types) throw new Error(`icns: no element type holds ${size} px (sizes: ${icnsSizes.join(', ')})`);
    for (const type of types) {
      const head = Buffer.alloc(8);
      head.write(type, 0, 'ascii'); head.writeUInt32BE(8 + data.length, 4);
      elements.push(head, data);
    }
  }
  const body = Buffer.concat(elements);
  const head = Buffer.alloc(8);
  head.write('icns', 0, 'ascii'); head.writeUInt32BE(8 + body.length, 4);
  return Buffer.concat([head, body]);
}

// A ZIP of the given entries in name order, stored (not compressed: a deflater's output may change between zlib
// versions, a stored entry never does; the PNGs and WebPs are compressed already), every entry dated
// 1980-01-01 00:00, the earliest DOS date, names in UTF-8.
export function zip(entries) {
  const sorted = [...entries].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const dosDate = (0 << 9) | (1 << 5) | 1;
  const locals = [], centrals = [];
  let offset = 0;
  for (const { name, data } of sorted) {
    const n = Buffer.from(name, 'utf8');
    const crc = zlib.crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(0, 8); local.writeUInt16LE(0, 10); local.writeUInt16LE(dosDate, 12);
    local.writeUInt32LE(crc, 14); local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(n.length, 26); local.writeUInt16LE(0, 28);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8); central.writeUInt16LE(0, 10); central.writeUInt16LE(0, 12);
    central.writeUInt16LE(dosDate, 14); central.writeUInt32LE(crc, 16); central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24); central.writeUInt16LE(n.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, n, data);
    centrals.push(central, n);
    offset += local.length + n.length + data.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(sorted.length, 8); end.writeUInt16LE(sorted.length, 10);
  end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}
