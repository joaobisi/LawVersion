# LawVersion ⚖️

O **LawVersion** é uma aplicação desktop descentralizada ponto a ponto (P2P) projetada para usuários cooperarem na edição de documentos jurídicos (`.docx`) com controle de versão local integrado (Git) e gerenciamento de concorrência distribuído (sinalização de travas por *leases* e sockets).

---

## 🖥️ Requisitos Mínimos de Sistema

Para a execução estável do LawVersion, a máquina de destino deve atender aos seguintes requisitos de hardware e software:

* **Processador (CPU):** Dual-Core de 1.6 GHz ou superior (Intel Core i3 de 3ª Geração, AMD Ryzen 3 ou equivalente). Suporte a arquitetura de instrução `x64` (64 bits).
* **Memória RAM:**
  * **Uso da aplicação:** ~80 MB a 150 MB de RAM ativa em execução.
  * **Mínimo livre no sistema:** 512 MB de RAM livre.
  * **Recomendado para o sistema:** 4 GB de RAM (para suportar o sistema operacional e o editor de textos aberto em paralelo).
* **Armazenamento (Espaço em Disco):**
  * **Binários da aplicação:** ~150 MB (se empacotado como *Self-Contained*) ou ~15 MB (se depender do runtime instalado).
  * **Espaço livre mínimo recomendado:** 500 MB livres (para suportar a expansão do histórico de commits e armazenamento do banco de dados Git local dos documentos de trabalho).
* **Sistemas Operacionais Compatíveis:**
  * **Windows:** Windows 10 (versão 1809 ou superior) ou Windows 11.
  * **Linux:** Ubuntu 20.04+, Debian 11+ ou Fedora 36+ (necessário ambiente desktop ativo e suporte às bibliotecas GTK3/libX11).
* **Dependências de Software (Externas):**
  * **Editor de Textos:** Microsoft Word (versão 2013 ou posterior) no Windows, ou LibreOffice Writer (versão 7.0 ou posterior) no Linux/Windows. O editor é necessário para que os arquivos de trava temporários (`~$` ou `.~lock`) sejam gerados no disco.
  * **Motor Git:** *Não é necessário instalar o cliente Git no sistema operacional*, pois o projeto utiliza a biblioteca nativa `LibGit2Sharp` que embarca a engine de versionamento diretamente no executável.

---

## 🚀 Como Executar em Modo de Desenvolvimento (Local)

Para testar a comunicação entre múltiplos usuários em uma mesma máquina física, você pode subir instâncias isoladas usando os scripts utilitários fornecidos na raiz do projeto.

### Pré-requisitos
Certifique-se de ter o **SDK do .NET 8.0** instalado na máquina.

### Executando no Linux
O script `run-p2p.sh` compila a aplicação e abre duas instâncias simultâneas do Avalonia UI em terminais separados:
```bash
chmod +x run-p2p.sh
./run-p2p.sh
```

### Executando no Windows
Abra o prompt de comando (cmd) ou PowerShell na pasta raiz do projeto e execute:
```cmd
run-p2p.bat
```

### Executando Manualmente
Se desejar iniciar uma instância manual via linha de comando, use o seguinte padrão de argumentos:
```bash
dotnet run --project LawVersion.UI -- "[Nome_do_Usuario]" [Porta_gRPC] "[Caminho_do_Diretorio_de_Trabalho]"
```
*Exemplo:*
```bash
dotnet run --project LawVersion.UI -- "Usuario_Joao" 5001 "$HOME/Documents/LawVersion_Joao"
```

---

## 📦 Como Empacotar e Publicar como Sistema (Produção)

Para publicar o LawVersion em modo de produção (sem necessitar do SDK do .NET instalado na máquina do usuário final), utilize o comando de publicação nativo do .NET.

### Compilação Autossuficiente (Self-Contained)
Gera um único executável que embarca todo o runtime do .NET 8 necessário, bastando ao usuário dar duplo clique para rodar.

* **Para Windows (x64):**
  ```cmd
  dotnet publish LawVersion.UI/LawVersion.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/win-x64
  ```

* **Para Linux (Ubuntu/Debian - x64):**
  ```bash
  dotnet publish LawVersion.UI/LawVersion.UI.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/linux-x64
  ```

### Parâmetros Importantes:
* `--self-contained true`: Embala o compilador e runtime do .NET junto com a aplicação.
* `-p:PublishSingleFile=true`: Compacta tudo em um único executável simples.
* `-p:PublishReadyToRun=true`: Otimiza a compilação no modo AOT (*Ahead-Of-Time*) para aceleração da inicialização inicial na máquina destino.
* `-o ./dist/[so]`: Caminho de saída onde o executável final será gravado.

---

## 📂 Estrutura do Workspace de Trabalho
Ao rodar, a aplicação cria e gerencia os seguintes elementos no diretório de trabalho:
* **Pasta de Trabalho (`WorkingDirectory`):** Onde ficam os arquivos `.docx` ativos para edição.
* **`.git/` (oculto):** Repositório local Git criado pelo `VersionControlService` para gerenciar o histórico de revisões.
* **`shares.json` (no AppData do usuário):** Arquivo de configuração que salva com quais parceiros cada arquivo do seu diretório foi compartilhado.
