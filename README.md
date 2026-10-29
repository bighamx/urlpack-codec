# URLPack Codec

将网址本身压缩进可还原的编码，提供一致的 **JavaScript / .NET 8** 实现，无需数据库、服务端映射或账号凭据。

该项目提取自实际运行的网址压缩服务，包含算法和独立测试，不包含原网站、登录系统、部署配置或业务数据。JavaScript 返回 `{ token, algorithm }`，不绑定任何站点域名。

## 快速使用

需要 Node.js 22+，不需要额外的 npm 运行时依赖：

```js
import { encodeUrl, decodeUrl, redirectUrl } from './src/index.js';

const original = 'https://www.google.com/search?q=hello%20world';
const { token, algorithm } = encodeUrl(original);
console.log(token, algorithm);
console.log(decodeUrl(token) === original); // true

// 基址由应用提供，算法本身不依赖此域名。
const link = 'https://your-domain.example/s/' + token;
// HTTP Location 使用 redirectUrl(decodeUrl(token)) 处理 Unicode / IDN。
```

JavaScript 依赖 Node 内置的 `node:buffer` 和 `node:zlib`，也适用于启用 `nodejs_compat` 的 Cloudflare Workers；当前入口不能直接当作浏览器脚本加载。

.NET 项目无需外部 NuGet 包：

```csharp
using UrlPack;

var original = "https://www.google.com/search?q=hello%20world";
var token = UrlCompressionCodec.Encode(original);
var restored = UrlCompressionCodec.Decode(token);
var location = UrlCompressionCodec.GetRedirectUrl(restored);
```

在应用项目中添加对 `dotnet/UrlPack/UrlPack.csproj` 的项目引用即可使用。

## 压缩方式

1. 对协议和 `www.` 等前缀进行标志编码。
2. 根据常用路径、参数词典及 Huffman 码，采用动态规划选择分段；数字和 `%XX` 转义字节专门打包。
3. 另设域名候选，使用约 1,400 个顶级域、2,000 个域名词条的静态 Huffman 码表。
4. 与原文及 Brotli 候选比较，输出最短编码。
5. 使用单段 URL 路径安全的 79 字符集表达二进制数据。

只接受不含用户名和密码的 HTTP/HTTPS 网址，最多 4096 个 UTF-8 字节；编码最多 5500 字符。解码有输出上限、流结束及填充检查。解码保留原始 UTF-8 内容，HTTP 跳转时才转换 Unicode 和 IDN。

## 格式与边界

- 编码没有外部版本标记；字典、模式位和字母表共同决定格式。改变它们可能使已生成的编码失效；请固定部署版本。
- 编码不是加密，也没有认证签名或校验和。任何持有者都能还原网址，格式检查不能检测所有内容篡改。
- 无法保证所有网址都缩短；随机令牌和已压缩数据尤其难压缩。区分原始 URL、编码长度和添加域名后的完整链接长度。
- 编码含 `&`、`+`、`$`、引号等合法路径字符。拼接 HTML、表单、JSON 和 shell 命令时，仍须遵循各自的转义规则；不要直接插入 HTML。代理和路由需要将整个 `/s/{token}` 交给解码器。
- 本项目不抓取目标网址，也不提供过期、单条撤销或修改目标地址的能力。部署公开跳转服务时由应用负责请求限制。

## 测试

```sh
npm test
dotnet run --project dotnet/UrlPack.Checks
```

两种语言验证同一组 47 个编码向量，另有随机往返测试。JavaScript 还检查非法输入和解压长度限制。向量使用公开或合成地址，不含真实访问控制状态或用户数据。

