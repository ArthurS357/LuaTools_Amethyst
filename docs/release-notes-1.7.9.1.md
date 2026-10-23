Versão de correção: o card do AmethystTool na aba Modo deixa de mostrar **Instalar** para uma cópia que já está na pasta
da Steam quando o PC liga numa rede conectada mas sem internet.

## Correções

- **Estado local primeiro.** O card pinta o que está no disco antes de esperar a consulta de versão. Antes, numa rede
  sem internet a consulta travava (em vez de falhar na hora) e o card ficava em "Instalar" durante minutos, mesmo com
  o AmethystTool instalado.
- **Consulta de versão com limite de 8 segundos.** Passou desse tempo, o app segue com a versão local. O download do
  arquivo continua com o limite maior.
- **Texto honesto offline.** Sem conexão e com os arquivos presentes, o card diz **Versão local (Offline)** e avisa
  "Sem conexão. Usando a versão instalada localmente.", em vez de afirmar que está em dia. O botão continua sendo
  **Reinstalar**.
- **Instalar ou atualizar sem internet.** A mensagem passa a ser "Sem conexão com a internet. Não é possível baixar uma
  nova versão." e o card volta ao estado anterior, sem ficar preso em "Instalando…".

## Nada mais mudou

Sem configuração nova, sem dependência nova e sem requisição de rede nova.

## Traduções

As três mensagens novas existem em inglês e português do Brasil; os demais idiomas usam o inglês até a próxima
rodada de tradução.
