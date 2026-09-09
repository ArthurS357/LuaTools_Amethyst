## Correções

- **Manifests iam para uma pasta que a Steam não lê.** O app escrevia em `config\depotcache`; a Steam lê
  `<Steam>\depotcache`, irmão de `steamapps`. O manifest ficava invisível, o depot pinado resolvia para nada
  e o download simplesmente não começava — sem erro em lugar nenhum. O caminho foi corrigido e o que já
  estava preso é movido no próximo início, em silêncio e sem sobrescrever nada.
- **Fixes Denuvo agora podem ser desfeitos.** Aplicar um fix registra quais arquivos ele substituiu ou
  adicionou e guarda backup de cada um, então Revert devolve o jogo ao estado anterior. Se algo mudou um
  desses arquivos depois — update do jogo, edição manual, ou um segundo fix por cima — o revert para e avisa
  em vez de sobrescrever.

## Novidades

- **Filtro "My games" na aba Fixes**, mostrando só os jogos para os quais você adicionou uma lua. A contagem
  no tooltip é quantos deles realmente têm fix, não o tamanho da biblioteca.
- Um fix já aplicado não se oferece para ser aplicado de novo, e um cujo jogo não está instalado explica por
  que está desabilitado.
- **Aviso sobre a mudança da Steam** nas abas Add e Builds. Numa manutenção de setembro de 2026 a Steam
  fechou o método usado para obter manifests de jogos não possuídos, e a saída é operacional, não técnica:

  1. Use uma fonte que **entregue os manifests** — **Sadie (Hubcap)** ou **Ryuu**.
  2. **Desligue "Auto Update Apps (Don't Lock Manifests)"** nas Configurações, para a lua ficar presa à
     versão baixada.
  3. Se já aparece "no internet" ou "unknown error", **re-adicione a lua** por uma dessas fontes com a
     opção desligada.

  O aviso é texto estático: não consulta rede, não checa estado e não registra nada. Fica nas duas telas
  onde ele importa — a Add, onde a fonte é escolhida, e o painel de depots, onde a falha aparece.

## Testes

- +36 testes (total: 1665). Os novos cobrem principalmente o que o revert **se recusa** a fazer: apagar um
  arquivo que outra coisa substituiu, seguir uma entrada de record que aponta para fora da pasta do jogo, ou
  deletar sem hash para verificar.
- Sete testes existentes fixavam o caminho errado de depotcache. Codificavam o bug e foram corrigidos.
- O aviso novo é coberto por quatro testes, sendo o principal o de que suas quatro chaves de texto
  **resolvem de verdade** — `Strings.Get` devolve o nome da chave quando ela não existe, então um typo
  apareceria na tela como `Notice_Manifests_Title` sem quebrar nada.

## Segurança

- Deletar um arquivo `added` no revert **exige** hash — mais restrito que o upstream, que deleta sem
  verificar quando o record não traz um.
- Contenção de caminho reaproveita `FixAnalyzer.IsContained`, tanto na extração quanto no revert.
- `AppliedFixIndexService` do upstream **não** foi portado: é write-only lá e criaria um registro persistente
  em `%AppData%` de tudo que o usuário corrigiu, sem benefício.
- O aviso novo é **estático**: não consulta rede, não lê estado, não persiste nada e não é dispensável
  (dispensar exigiria um campo em `settings.json`, e todo campo novo tem de entrar no predicado `empty` do
  `SaveCore` — risco desnecessário para um cartão informativo).
- Nenhuma telemetria, auto-update, elevação UAC ou envio de chaves reintroduzido.
- Formato de `settings.json` inalterado. Nenhuma dependência NuGet nova.
