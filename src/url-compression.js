import { Buffer } from 'node:buffer';
import { brotliCompressSync, brotliDecompressSync, constants } from 'node:zlib';
import { packToken, unpackToken } from './url-token-encoding.js';
import { encodeDomain, decodeDomain } from './url-domain-compression.js';

// Shared wire format with the .NET implementation; cross-runtime fixtures cover it.
const prefixes = ['', 'https://www.', 'https://', 'http://www.', 'http://'];
const words = [
  '.com', '.org', '.net', '.cn', '.io', '.co', '.edu', '.gov', '.html', '.php',
  'www.', 'api.', 'github.com', 'google.com', 'youtube.com', 'youtu.be', 'wikipedia.org',
  'microsoft.com', 'cloudflare.com', 'xbyham.com', 'qq.com', 'baidu.com', 'weixin.qq.com',
  'bilibili.com', 'taobao.com', 'amazon.com', 'stackoverflow.com', 'twitter.com', 'facebook.com',
  '/index.html', '/index.php', '/Index', '/search', '/article/', '/articles/', '/detail/',
  '/details/', '/video/', '/watch', '/download', '/api/', '/v1/', '/v2/', '/images/',
  '?q=', '?id=', '?v=', '&page=', '?page=', '&sort=', '&language=', '&category=',
  'utm_source=', 'utm_medium=', 'utm_campaign=', 'redirect_uri=', 'returnUrl=', 'https://',
  'http://', 'token=', 'query=', 'search=', 'index', 'default', 'category', 'language',
  'technology', 'descending', 'ascending', 'zh-CN', 'UTF-8', '.json', '.aspx', '.jpg', '.png'
];
const end = 256;
const number = end + 1 + words.length;
const upper = number + 1;
const lower = number + 2;
const bytes = words.map(word => Buffer.from(word, 'ascii'));
const common = 'etaoinshrdlucmfpgywbvkxjqz';

function buildTree() {
  const queue = [];
  for (let symbol = 0; symbol <= lower; symbol++) {
    let weight = 1;
    const rank = symbol < 256 ? common.indexOf(String.fromCharCode(symbol)) : -1;
    if (rank >= 0) {
      weight = 120 - rank * 3;
    } else if (symbol >= 48 && symbol <= 57) {
      weight = 25;
    } else if (symbol < 256 && '/.-_?=&%:#~'.includes(String.fromCharCode(symbol))) {
      weight = 45;
    } else if (symbol >= 65 && symbol <= 90) {
      weight = 10;
    } else if (symbol > end && symbol < number) {
      weight = symbol === end + 1 ? 240 : 25;
    } else if (symbol >= number) {
      weight = 50;
    }
    queue.push({ symbol, weight, order: symbol });
  }
  let order = lower + 1;
  while (queue.length > 1) {
    queue.sort((a, b) => a.weight - b.weight || a.order - b.order);
    const left = queue.shift();
    const right = queue.shift();
    queue.push({ symbol: -1, weight: left.weight + right.weight, order: order++, left, right });
  }
  return queue[0];
}
const tree = buildTree();
const codes = [];
function visit(node, code) {
  if (node.symbol >= 0) {
    codes[node.symbol] = code;
    return;
  }
  visit(node.left, code + '0');
  visit(node.right, code + '1');
}
visit(tree, '');
const decimalBits = Array.from({ length: 19 }, (_, count) => count ? (10n ** BigInt(count) - 1n).toString(2).length : 0);
const minimumBitsPerByte = Math.min(
  ...codes.slice(0, 256).map(code => code.length),
  ...words.map((word, index) => codes[end + 1 + index].length / word.length),
  ...decimalBits.slice(1).map((width, index) => (codes[number].length + 5 + width) / (index + 1)),
  (codes[upper].length + 5 + 256) / 96,
  (codes[lower].length + 5 + 256) / 96
);

function encodeDictionary(input, prefix) {
  const costs = new Int32Array(input.length + 1);
  const choices = new Array(input.length);
  costs[input.length] = codes[end].length;
  function hex(value, uppercase) {
    return value >= 48 && value <= 57 || (uppercase ? value >= 65 && value <= 70 : value >= 97 && value <= 102);
  }
  for (let offset = input.length - 1; offset >= 0; offset--) {
    costs[offset] = codes[input[offset]].length + costs[offset + 1];
    choices[offset] = { symbol: input[offset], length: 1, count: 0 };
    function consider(symbol, length, extra, count) {
      const cost = codes[symbol].length + extra + costs[offset + length];
      if (cost < costs[offset]) {
        costs[offset] = cost;
        choices[offset] = { symbol, length, count };
      }
    }
    for (let word = 0; word < bytes.length; word++) {
      const candidate = bytes[word];
      if (input[offset] !== candidate[0] || offset + candidate.length > input.length) {
        continue;
      }
      let matches = true;
      for (let index = 1; index < candidate.length; index++) {
        if (input[offset + index] !== candidate[index]) {
          matches = false;
          break;
        }
      }
      if (matches) {
        consider(end + 1 + word, candidate.length, 0, 0);
      }
    }
    for (let count = 1; count <= 18 && offset + count <= input.length; count++) {
      const digit = input[offset + count - 1];
      if (digit < 48 || digit > 57) {
        break;
      }
      consider(number, count, 5 + decimalBits[count], count);
    }
    for (let casing = 0; casing < 2; casing++) {
      for (let count = 1; count <= 32 && offset + count * 3 <= input.length; count++) {
        const start = offset + (count - 1) * 3;
        if (input[start] !== 37 || !hex(input[start + 1], casing === 0) || !hex(input[start + 2], casing === 0)) {
          break;
        }
        consider(casing === 0 ? upper : lower, count * 3, 5 + count * 8, count);
      }
    }
  }
  const output = [];
  let bitCount = 0;
  let accumulator = 0;
  function bits(value, count) {
    value = BigInt(value);
    for (let bit = count - 1; bit >= 0; bit--) {
      accumulator = (accumulator << 1) | Number((value >> BigInt(bit)) & 1n);
      if (++bitCount === 8) {
        output.push(accumulator);
        accumulator = 0;
        bitCount = 0;
      }
    }
  }
  function code(symbol) {
    for (const bit of codes[symbol]) {
      bits(bit === '1' ? 1 : 0, 1);
    }
  }
  bits(8 | prefix, 5);
  for (let offset = 0; offset < input.length;) {
    const choice = choices[offset];
    code(choice.symbol);
    if (choice.symbol === number) {
      bits(choice.count - 1, 5);
      bits(BigInt(input.subarray(offset, offset + choice.count).toString('ascii')), decimalBits[choice.count]);
    } else if (choice.symbol === upper || choice.symbol === lower) {
      bits(choice.count - 1, 5);
      for (let index = 0; index < choice.count; index++) {
        const start = offset + index * 3 + 1;
        bits(Number.parseInt(input.subarray(start, start + 2).toString('ascii'), 16), 8);
      }
    }
    offset += choice.length;
  }
  code(end);
  if (bitCount) {
    output.push(accumulator << (8 - bitCount));
  }
  return Buffer.from(output);
}

export function encodeUrl(url) {
  validateUrl(url);
  let prefix = 0;
  for (let index = 1; index < prefixes.length; index++) {
    if (url.startsWith(prefixes[index])) {
      prefix = index;
      break;
    }
  }
  const input = Buffer.from(url.slice(prefixes[prefix].length), 'utf8');
  const raw = Buffer.concat([Buffer.from([prefix << 3]), input]);
  const compressed = brotliCompressSync(input, { params: {
    [constants.BROTLI_PARAM_QUALITY]: 5,
    [constants.BROTLI_PARAM_LGWIN]: 16
  } });
  const candidates = [raw, Buffer.concat([Buffer.from([128 | (prefix << 3)]), compressed])];
  const rawToken = packToken(raw);
  let token = rawToken;
  for (const candidate of candidates.slice(1)) {
    const encoded = packToken(candidate);
    if (encoded.length < token.length) {
      token = encoded;
    }
  }
  // A conservative lower bound lets highly repetitive inputs skip the JS parser.
  const lowerBound = Math.floor((5 + input.length * minimumBitsPerByte + codes[end].length) / Math.log2(79));
  if (lowerBound <= token.length) {
    const packed = packToken(encodeDictionary(input, prefix));
    // The .NET encoder gives dictionary mode priority over Brotli for equal lengths.
    if (packed.length < token.length || (packed.length === token.length && token !== rawToken)) {
      token = packed;
    }
  }
  const hostEnd = input.findIndex(value => value === 47 || value === 63 || value === 35 || value === 58);
  const tailLength = hostEnd < 0 ? 0 : input.length - hostEnd;
  const domainLowerBound = Math.floor((tailLength * minimumBitsPerByte + 6) / Math.log2(79));
  const domain = domainLowerBound <= token.length ? encodeDomain(input, prefix, encodeDictionary) : null;
  if (domain) {
    const candidate = packToken(domain);
    if (candidate.length < token.length || (candidate.length === token.length && (unpackToken(token)[0] >> 6) === 2)) {
      token = candidate;
    }
  }
  const mode = unpackToken(token)[0] >> 6;
  return { token, algorithm: ['原文编码', 'URL 词典 / 数字段压缩', 'Brotli', '域名词典 / 分段压缩'][mode] };
}

function validateUrl(url) {
  if (typeof url !== 'string' || !url || Buffer.byteLength(url, 'utf8') > 4096 || /[\s\p{Cc}\\]/u.test(url)) {
    throw new Error('请输入有效的 HTTP/HTTPS 网址，长度不能超过 4096 个 UTF-8 字节');
  }
  if (Buffer.from(url, 'utf8').toString('utf8') !== url) {
    throw new Error('网址包含无效 Unicode 字符');
  }
  const parsed = new URL(url);
  if (!/^https?:\/\//i.test(url) || !['http:', 'https:'].includes(parsed.protocol) || !parsed.hostname || parsed.username || parsed.password) {
    throw new Error('仅支持不含用户名和密码的 HTTP/HTTPS 网址');
  }
}

function dictionary(data) {
  let position = 5;
  const output = [];
  function bits(count) {
    if (position + count > data.length * 8) {
      throw new Error('Incomplete data');
    }
    let value = 0n;
    for (let index = 0; index < count; index++) {
      value = (value << 1n) | BigInt((data[position >> 3] >> (7 - position % 8)) & 1);
      position++;
    }
    return value;
  }
  function append(part) {
    if (output.length + part.length > 4096) {
      throw new Error('URL too long');
    }
    for (const value of part) {
      output.push(value);
    }
  }
  while (true) {
    let node = tree;
    while (node.symbol < 0) {
      node = bits(1) === 0n ? node.left : node.right;
    }
    if (node.symbol === end) {
      const remaining = data.length * 8 - position;
      if (remaining >= 8 || bits(remaining) !== 0n) {
        throw new Error('Trailing data');
      }
      return Buffer.from(output);
    }
    if (node.symbol < end) {
      append([node.symbol]);
    } else if (node.symbol < number) {
      append(bytes[node.symbol - end - 1]);
    } else if (node.symbol === number) {
      const count = Number(bits(5)) + 1;
      if (count > 18) {
        throw new Error('Invalid number length');
      }
      const maximum = 10n ** BigInt(count);
      const width = (maximum - 1n).toString(2).length;
      const value = bits(width);
      if (value >= maximum) {
        throw new Error('Invalid number');
      }
      append(Buffer.from(value.toString().padStart(count, '0'), 'ascii'));
    } else {
      const count = Number(bits(5)) + 1;
      const alphabet = node.symbol === upper ? '0123456789ABCDEF' : '0123456789abcdef';
      let escaped = '';
      for (let index = 0; index < count; index++) {
        const value = Number(bits(8));
        escaped += '%' + alphabet[value >> 4] + alphabet[value & 15];
      }
      append(Buffer.from(escaped, 'ascii'));
    }
  }
}

export function decodeUrl(token) {
  const data = unpackToken(token);
  const mode = data[0] >> 6;
  const prefix = (data[0] >> 3) & 7;
  if (prefix > 4 || ((mode === 0 || mode === 2) && (data[0] & 7) !== 0)) {
    throw new Error('Invalid header');
  }
  let body;
  if (mode === 0) {
    body = data.subarray(1);
  } else if (mode === 1) {
    body = dictionary(data);
  } else if (mode === 3) {
    body = decodeDomain(data, dictionary);
  } else {
    const result = brotliDecompressSync(data.subarray(1), { maxOutputLength: 4096, info: true });
    if (result.engine.bytesWritten !== data.length - 1) {
      throw new Error('Trailing Brotli data');
    }
    body = result.buffer;
  }
  const url = prefixes[prefix] + new TextDecoder('utf-8', { fatal: true }).decode(body);
  validateUrl(url);
  return url;
}

export function redirectUrl(url) {
  const start = url.indexOf('://') + 3;
  const suffix = url.slice(start).search(/[/?#]/);
  const finish = suffix < 0 ? url.length : start + suffix;
  const authority = url.slice(start, finish);
  if (/[^\x00-\x7f]/.test(authority)) {
    const port = authority.lastIndexOf(':');
    const originalPort = port < 0 ? '' : authority.slice(port);
    url = url.slice(0, start) + new URL(url).hostname + originalPort + url.slice(finish);
  }
  return url.replace(/[^\x00-\x7f]/gu, value => encodeURIComponent(value));
}
