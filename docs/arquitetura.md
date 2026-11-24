# Decisões de arquitetura

## Atomicidade no DynamoDB

O caso de uso é uma loja fictícia por sessão, com volume limitado. O estado de cada sessão é persistido em um item compactado com uma revisão usada pela escrita condicional. Dessa forma, um lote e seus itens ficam visíveis na mesma gravação, e alterações concorrentes são reavaliadas contra a versão atual da sessão. A chave de sessão separa o estoque, os vínculos e o histórico de cada visitante.

O índice de vencimentos guarda apenas os dados necessários para encontrar sessões com trabalho pendente. Uma Lambda pode receber mais de um aviso da mesma sessão. O token de reserva protege a conclusão, e o vencimento permite retomar trabalho interrompido. O limite de tamanho da sessão faz parte do modelo da demo.

## Confirmação no destino

Cada atualização transporta uma quantidade absoluta, um ID de operação e versões de origem e destino. O simulador registra a operação na mesma alteração que aplica o saldo. Uma operação repetida pode consultar esse registro mesmo quando a primeira resposta se perdeu. Uma versão antiga é superada quando uma nova correção já a substituiu. Uma alteração feita no anúncio depois da prévia pode gerar conflito e exigir nova revisão pelo operador.

O painel distingue **aguardando confirmação**, **sincronizado**, **conflito**, **superado** e **falha**. O estado sincronizado representa uma aplicação confirmada, e a tela de divergências mostra o saldo observado mais recente.

## Execução local e hospedada

Localmente, um serviço em segundo plano procura trabalhos vencidos no DynamoDB Local. Na AWS, SQS antecipa o processamento e o EventBridge desperta o worker periodicamente para recuperar avisos perdidos. A fila de mensagens mortas registra falhas de processamento da infraestrutura. O histórico visível registra o resultado de negócio por SKU.
