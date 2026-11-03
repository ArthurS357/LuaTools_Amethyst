A maior atualização do fork até hoje: janela de vidro com Acrylic, backup de saves de qualquer lugar, badge "Instalado",
auto-update só pelo GitHub com SHA-256 conferido, CloudRedirect removido e o app inteiro traduzido. Reúne as builds de
teste 1.7.12.1 a 1.7.12.10.

## Novidades

- **Efeito vidro (Configurações → Geral, ligado por padrão).** No Windows 11 a barra de título e o menu lateral ficam
  translúcidos sobre um fundo Acrylic desfocado (shell a 65% de opacidade, para o desfoque aparecer). As páginas
  continuam opacas, então o texto mantém o contraste. A troca é ao vivo, sem reiniciar. Windows 10 e alto contraste
  mantêm a janela sólida. Usa mais GPU: desligue em PCs mais lentos.
- **Backup de saves de vários lugares.** A janela lista todos os lugares onde o jogo guarda saves (userdata da Steam,
  Documents, My Games, Saved Games, AppData Local, LocalLow e Roaming), com o tamanho de cada um, e você marca quais
  entram. Um único zip guarda tudo e **Restaurar** devolve cada pasta ao seu lugar; zips antigos continuam restaurando.
  **Adicionar pasta manualmente…** registra uma pasta para o jogo (também em Configurações).
- **Backups organizados.** Uma pasta por jogo, pasta de backups configurável (OneDrive, outro disco; **Resetar para
  auto** volta ao padrão) e **Importar save (.zip)…** de qualquer lugar, com as mesmas verificações do restore e cópia
  dos saves atuais antes.
- **Badge "Instalado".** No Gerenciar, jogos que a Steam tem inteiros no disco ganham um badge verde, e a ordenação
  padrão passa a ser **Instalados primeiro**.
- **Aba Modo e boas-vindas.** O card volta a ser BetterSteamTools (`madoiscool/BetterSteamTools`), com aviso sobre a
  telemetria e o sistema DonateKeys. A tela de boas-vindas instala só o AmethystTool e o frontend Amethyst.
- **Traduções.** As 272 mensagens que ainda estavam só em inglês foram traduzidas nos 29 idiomas (com apoio de IA,
  aguardando revisão de falantes nativos).

## Correções

- **Limpeza de backups (Configurações → Manutenção).** Um backup com menos de 5 minutos não é mais apagado (pode ser de
  uma instalação em andamento), um lote recusado diz que nada foi apagado e qual item o recusou, e o card do Modo para de
  mostrar uma pasta de backup que já foi apagada.
- **Diagnóstico.** 49 lugares que engoliam erros em silêncio agora gravam a causa no log.
- **Desempenho.** Capas decodificadas no tamanho em que aparecem, lista de ~341 mil jogos e detalhes da Steam lidos por
  stream, e o script da loja deixou de ser remontado a cada segundo.

## Segurança

- **Auto-update direto do GitHub, com hash conferido (F-04).** A nova versão só é baixada de `github.com` /
  `api.github.com` por https, do repositório do projeto, nunca por mirror. Antes de aceitar o pacote, o SHA-256 dele é
  comparado com o digest que o GitHub publica; sem digest, GitHub inacessível, arquivo não listado ou hash diferente, a
  atualização é recusada e a versão atual fica. Onde o github.com é bloqueado, atualize à mão pela página de releases.
- **CloudRedirect removido.** O projeto não existe mais e está fora da política de privacidade e confiança do fork. O app
  não baixa, executa, detecta nem oferece mais. Se uma build antiga deixou `cloud_redirect.dll` ao lado do `steam.exe`,
  ele fica lá intocado; apague à mão se não quiser.
- Nenhuma telemetria, elevação UAC, SAC ou envio de chaves foi reintroduzido.

## Build e testes

- SDK fixado em `global.json`, fins de linha por `.gitattributes` e versões de pacote em um só lugar (Central Package
  Management, F-17). Nenhuma dependência nova.
- Suíte de testes completa verde na build de lançamento.
