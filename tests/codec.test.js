import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import { brotliCompressSync } from 'node:zlib';
import { encodeUrl, decodeUrl, redirectUrl } from '../src/index.js';
import { packToken, alphabet } from '../src/url-token-encoding.js';

const vectors = JSON.parse(readFileSync(new URL('./vectors.json', import.meta.url), 'utf8'));
for (const { url, token, redirect } of vectors) {
  assert.equal(encodeUrl(url).token, token);
  assert.equal(decodeUrl(token), url);
  assert.equal(redirectUrl(url), redirect);
  assert.ok([...token].every(character => alphabet.includes(character)));
}
for (let index = 0; index < 200; index++) {
  const url = 'https://example.org/search?q=' + randomBytes(1 + index).toString('hex');
  assert.equal(decodeUrl(encodeUrl(url).token), url);
}
for (const url of ['javascript:alert(1)', 'ftp://example.com', 'https://user:pass@example.com', 'https://example.com/a b']) {
  assert.throws(() => encodeUrl(url));
}
for (const token of ['', '!', 'abc/def', 'a'.repeat(5501), '2VT17hQfmqD0qH-E67PnA']) {
  assert.throws(() => decodeUrl(token));
}
const bomb = brotliCompressSync(Buffer.from('https://example.com/' + 'a'.repeat(1000000)));
assert.throws(() => decodeUrl(packToken(Buffer.concat([Buffer.from([128]), bomb]))));
console.log('JavaScript: ' + vectors.length + ' shared vectors, 200 random roundtrips, invalid input and bounded decompression passed.');
