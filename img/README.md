# img/

存放体积较大的离线资源(如容器镜像 tar 包),**不纳入 Git 仓库**(见根目录 `.gitignore` 的 `img/*.tar`)。

例如 Docker 页面用到的 speedtest-x 测速镜像,可自行构建后放置于此:

```bash
docker pull badapple9/speedtest-x
docker save badapple9/speedtest-x -o "img/badapple9_speedtest-x(latest).tar"
```
