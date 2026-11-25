# Decisões de arquitetura

## Atomicidade no DynamoDB

O caso de uso é uma loja fictícia por sessão, com volume limitado. O estado de cada sessão é persistido em um item compactado com uma revisão usada pela escrita condicional. Dessa forma, um lote e seus itens ficam visíveis na mesma gravação, e alterações concorrentes são reavaliadas contra a versão atual da sessão. A chave de sessão separa o estoque, os vínculos e o histórico de cada visitante.

O índice de vencimentos guarda apenas os dados necessários para encontrar sessões com trabalho pendente. Uma Lambda pode receber mais de um aviso da mesma sessão. O token de reserva protege a conclusão, e o vencimento permite retomar trabalho interrompido. O limite de tamanho da sessão faz parte do modelo da demo.

## Confirmação no destino

Cada atualização transporta uma quantidade absoluta, um ID de operação e versões de origem e destino. O simulador registra a operação na mesma alteração que aplica o saldo. Uma operação repetida pode consultar esse registro mesmo quando a primeira resposta se perdeu. Uma versão antiga é superada quando uma nova correção já a substituiu. Uma alteração feita no anúncio depois da prévia pode gerar conflito e exigir nova revisão pelo operador.

O painel distingue **aguardando confirmação**, **sincronizado**, **conflito**, **superado** e **falha**. O estado sincronizado representa uma aplicação confirmada, e a tela de divergências mostra o saldo observado mais recente.

## Distribuição entre dois canais

Cada plano fixa os dois anúncios, suas versões observadas e as quantidades desejadas. O operador pode deixar parte do estoque livre. A API grava vínculos, plano, lote e chave de idempotência na mesma escrita condicional. Durante a execução, o SKU aceita um único lote ativo. Requisições de canal único para um SKU distribuído exigem revisão no painel de distribuição.

Quando um canal reduz e o outro aumenta, a operação de aumento referencia o ID da redução e aguarda sem prazo de execução. A confirmação da redução e a liberação do aumento ocorrem na mesma gravação. Uma resposta perdida exige consultar o recibo. Se a redução falhar ou entrar em conflito, o aumento fica bloqueado para revisão. Uma nova captura do ERP pode superar o plano. Antes de aplicar um aumento, o destino simulado verifica o saldo do outro anúncio e o limite atual do ERP.

Essa última verificação usa o estado compartilhado pelos dois simuladores. Uma integração com canais independentes precisaria considerar vendas e mudanças externas que ocorressem entre a leitura e a publicação. Sessões antigas recebem os anúncios do segundo canal ao abrir a distribuição. Para um rollback que anteceda o suporte multicanal, suspenda novas distribuições e deixe expirar as sessões de duas horas antes de restaurar o código antigo.

## Execução local e hospedada

Localmente, um serviço em segundo plano procura trabalhos vencidos no DynamoDB Local. Na AWS, SQS antecipa o processamento e o EventBridge desperta o worker periodicamente para recuperar avisos perdidos. A fila de mensagens mortas registra falhas de processamento da infraestrutura. O histórico visível registra o resultado de negócio por SKU.
