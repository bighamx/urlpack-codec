import { Buffer } from 'node:buffer';
import tables from './url-domain-codes.json' with { type: 'json' };

const domains = Object.keys(tables.sldEncode).sort((a, b) => b.length - a.length);
const invert = table => new Map(Object.entries(table).map(([key, value]) => [value, key]));
const tlds = invert(tables.tldEncode);
const slds = invert(tables.sldEncode);
const characters = invert(tables.domainEncode);

export function encodeDomain(input, prefix, encodeTail) {
  const body = input.toString('utf8');
  let end = body.search(/[/?#:]/);
  if (end < 0) {
    end = body.length;
  }
  const host = body.slice(0, end);
  const dot = host.lastIndexOf('.');
  const tld = host.slice(dot + 1);
  if (dot < 0 || !tables.tldEncode[tld]) {
    return null;
  }
  const remaining = host.slice(0, dot);
  const sld = domains.find(value => remaining.endsWith(value));
  const literal = sld ? remaining.slice(0, -sld.length) : remaining;
  if ([...literal].some(character => !tables.domainEncode[character])) {
    return null;
  }
  let bits = ((3 << 3) | prefix).toString(2).padStart(5, '0') + (sld ? '1' : '0');
  bits += tables.tldEncode[tld];
  if (sld) {
    bits += tables.sldEncode[sld];
  }
  for (const character of literal) {
    bits += tables.domainEncode[character];
  }
  bits += tables.domainEncode.END;
  bits = bits.padEnd(Math.ceil(bits.length / 8) * 8, '0');
  const output = Buffer.alloc(bits.length / 8);
  for (let offset = 0; offset < bits.length; offset += 8) {
    output[offset / 8] = Number.parseInt(bits.slice(offset, offset + 8), 2);
  }
  return Buffer.concat([output, encodeTail(Buffer.from(body.slice(end), 'utf8'), 0)]);
}

export function decodeDomain(data, decodeTail) {
  let position = 5;
  function bit() {
    if (position >= data.length * 8) {
      throw new Error('Incomplete domain data');
    }
    const value = (data[position >> 3] >> (7 - position % 8)) & 1;
    position++;
    return value;
  }
  function read(table) {
    let code = '';
    for (let count = 0; count < 32; count++) {
      code += bit() ? '1' : '0';
      if (table.has(code)) {
        return table.get(code);
      }
    }
    throw new Error('Invalid domain code');
  }
  const known = bit();
  const tld = read(tlds);
  const sld = known ? read(slds) : '';
  let literal = '';
  while (literal.length <= 255) {
    const value = read(characters);
    if (value === 'END') {
      break;
    }
    literal += value;
  }
  if (literal.length > 255) {
    throw new Error('Domain too long');
  }
  while (position % 8) {
    if (bit()) {
      throw new Error('Nonzero domain padding');
    }
  }
  const tail = data.subarray(position / 8);
  if (!tail.length || (tail[0] >> 3) !== 8) {
    throw new Error('Invalid domain suffix');
  }
  const output = Buffer.concat([Buffer.from(literal + sld + '.' + tld, 'ascii'), decodeTail(tail)]);
  if (output.length > 4096) {
    throw new Error('URL too long');
  }
  return output;
}
