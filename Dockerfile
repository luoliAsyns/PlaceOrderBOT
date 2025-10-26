
# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG COMMON_REPO=https://github.com/luoliAsyns/Common.git

RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

# 设置工作目录为项目根目录（Dockerfile所在目录）
WORKDIR /src


# 克隆Common仓库
# 使用GitHub token进行认证，避免公共仓库的API限制或访问私有仓库
RUN if [ -n "$GITHUB_TOKEN" ]; then \
        git clone https://$GITHUB_TOKEN@$(echo $COMMON_REPO | sed 's/^https:\/\///') Common; \
    else \
        git clone $COMMON_REPO Common; \
    fi
    
RUN mv /src/Common /Common/

COPY . ./PlaceOrderBOT/

# 确保工作目录正确指向项目文件
WORKDIR "/src/PlaceOrderBOT"


# 先还原依赖，确保能找到Common项目
RUN dotnet restore "./PlaceOrderBOT.csproj"

# 构建项目
RUN dotnet build "./PlaceOrderBOT.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 发布阶段
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PlaceOrderBOT.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PlaceOrderBOT.dll"]

