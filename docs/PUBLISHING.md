# 上传 GitHub

此目录是独立的 Git 仓库，初始分支为 `codex/github-ready`。以下步骤用于首次发布；已有提交或 `origin` 时无需重复创建。

## 发布前

1. 仓库采用 MIT 协议，版权署名为 `日月汐 (RiYueXi)`；提交源码时保留根目录的 `LICENSE`。
2. 在 Windows x64 上安装 .NET 9 SDK，执行 `./build.ps1` 和 `./test.ps1`。
3. 查看 `git status --short`。构建输出、缓存、测试临时文件、IDE 私人配置和证书已被 `.gitignore` 排除。

## 推送源码

在 GitHub 上新建空仓库，例如 `PackPaste`。不要在网页上预先生成 README、LICENSE 或 .gitignore，以免与本地初始提交产生冲突。

在本目录打开终端，然后运行：

```powershell
git status --short
git add .
git diff --cached --stat
git commit -m "Initial source release v1.0.1"
git remote add origin https://github.com/RiYueXi/PackPaste.git
git push -u origin HEAD:main
```

上面的远程地址指向 `RiYueXi/PackPaste`；Fork 后请换成自己的仓库地址。最后一条命令把当前本地分支推送为 GitHub 的 `main`。Git 提交若提示缺少身份信息，请填写你自己的姓名和邮箱；可以使用 GitHub 提供的隐私邮箱。

也可在 GitHub Desktop 中选择“添加已有本地仓库”，指向此目录后提交并发布。通过网页上传时只上传源码文件，不上传 `.git` 目录。

## 发布安装程序

安装程序放在 **GitHub Releases** 附件中，不提交到源码仓库。运行 `build.ps1` 后，可以发布 `v1.0.1`，附上：

- `dist/PackPaste-1.0.1-win-x64-Setup.exe`
- `dist/SHA256SUMS.txt`
- `dist/使用说明.md`
- `dist/LICENSE`（构建脚本也会把许可证嵌入安装载荷，安装到程序目录）。

版本说明可参考根目录的 `CHANGELOG.md`。
