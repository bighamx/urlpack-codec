import { Buffer } from 'node:buffer';

export const alphabet = "!$&'()*+,-.0123456789:;=~@ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

export function packToken(data) {
  let number = BigInt('0x01' + data.toString('hex'));
  let encoded = '';
  while (number > 0n) {
    encoded = alphabet[Number(number % 79n)] + encoded;
    number /= 79n;
  }
  return encoded;
}

export function unpackToken(token) {
  if (typeof token !== 'string' || token.length < 2 || token.length > 5500) {
    throw new Error('Invalid token length');
  }
  if (token[0] === alphabet[0]) {
    throw new Error('Invalid token');
  }
  let number = 0n;
  for (const character of token) {
    const digit = alphabet.indexOf(character);
    if (digit < 0) {
      throw new Error('Invalid alphabet');
    }
    number = number * 79n + BigInt(digit);
  }
  let hex = number.toString(16);
  if (hex.length % 2) {
    hex = '0' + hex;
  }
  if (!hex.startsWith('01') || hex.length < 4) {
    throw new Error('Invalid sentinel');
  }
  return Buffer.from(hex.slice(2), 'hex');
}
