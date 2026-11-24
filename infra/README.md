# Infraestrutura da demonstração

O template SAM cria um bucket S3 privado para o build do React, duas Lambdas .NET 11 para a API e o worker, DynamoDB provisionado, SQS com fila de falhas e um reconciliador EventBridge Scheduler a cada cinco minutos. A distribuição CloudFront serve o React e encaminha `/api/*` sem cache para a Function URL autenticada por IAM. Duas origens com OAC restringem as chamadas de serviços à própria distribuição.

## Antes de publicar

- Use a região `us-east-1` e o nome de stack `syncforge-demo`. Se precisar de outro nome, use até 30 caracteres minúsculos, números e hífens, começando por uma letra. Use o mesmo nome no bootstrap e no deploy da aplicação.
- Consulte a modalidade da conta e as franquias restantes antes de habilitar a demonstração. DynamoDB usa 15 RCU/WCU na tabela e 5 no índice. A franquia provisionada é compartilhada e o mesmo vale para as franquias de Lambda, Scheduler, logs e SQS. Os alertas de uso e de orçamento avisam sobre consumo. Configure-os antes de abrir a demonstração.
- Escolha `CloudFrontMode=free-flat-rate` em conta elegível ao plano CloudFront de US$ 0 por mês. O template ativa a assinatura e a regra WAF para limitar requisições de API por IP. Antes de enviar o frontend, confira a assinatura ativa. Contas no plano **AWS Free Account** não são elegíveis a esse plano do CloudFront.
- Use `CloudFrontMode=pay-as-you-go` para **AWS Free Account** somente depois de confirmar que as franquias e os créditos temporários da conta cobrem a demonstração. Nesse modo, o template não cria WAF nem assinatura. No fim dos créditos ou da janela da conta gratuita, a AWS pode suspender os recursos. Fora das franquias aplicáveis, a AWS pode cobrar conforme o plano da conta.
- Use exclusivamente os CSVs fictícios da demonstração. O upload vai para a API, que persiste os dados normalizados no DynamoDB. O bucket do frontend guarda somente artefatos estáticos. A expiração de cada sessão é verificada pela API. A remoção física pelo TTL do DynamoDB é assíncrona.

## Preparar o acesso OIDC

Execute o bootstrap uma vez, com uma identidade AWS autorizada a provisionar os recursos abaixo. Informe o ARN do provedor OIDC existente para o GitHub. O bootstrap cria recursos dedicados ao SyncForge e apenas referencia o provedor existente. No GitHub, configure o ambiente `aws-demo` para permitir deploy exclusivamente a partir da branch protegida `main`.

```bash
region=us-east-1
stack=syncforge-demo
bootstrap=syncforge-demo-bootstrap
repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"
oidc_prefix="$(gh api "repos/$repo/actions/oidc/customization/sub" --jq '.sub_claim_prefix // empty')"
oidc_subject="${oidc_prefix:-repo:$repo}:environment:aws-demo"

aws cloudformation deploy --template-file infra/bootstrap.yaml \
  --stack-name "$bootstrap" --region "$region" --capabilities CAPABILITY_NAMED_IAM \
  --parameter-overrides "AppStackName=$stack" \
    "GitHubOidcProviderArn=$GITHUB_OIDC_PROVIDER_ARN" \
    "GitHubOidcSubject=$oidc_subject"
```

O assunto OIDC inclui os IDs imutáveis do proprietário e do repositório quando o GitHub emite esse formato. O comando consulta o prefixo configurado e vincula a confiança ao ambiente ws-demo.

O output `DeployRoleArn` identifica a role assumida pelo workflow por OIDC. Essa role pode operar o stack da aplicação, ler os outputs do bootstrap e enviar artefatos aos dois buckets dedicados. Ela passa apenas a role `CloudFormationRoleArn` ao CloudFormation. A role do stack passa somente as roles prefixadas do SyncForge às Lambdas e ao Scheduler, com um limite de permissões de runtime. Os dois OACs pertencem ao bootstrap.

## Empacotar e publicar

Execute os comandos a partir da raiz, em Linux, com SDK .NET 11, AWS CLI e SAM CLI. O diretório `infra/build` é gerado. Cada pacote inclui o runtime e um `bootstrap` Linux x64 executável que o Lambda carrega no ambiente `provided.al2023`. A versão do .NET é atualizada reconstruindo os pacotes com um SDK atualizado.

```bash
aws freetier get-account-plan-state --region "$region"
bash infra/prepare-lambda.sh
sam validate --lint --template-file infra/template.yaml --region "$region"
```

Com a modalidade verificada, passe os outputs do bootstrap ao deploy. Este exemplo usa a role de CloudFormation do bootstrap, que provisiona o modo `pay-as-you-go`. Para uma conta elegível que use o modo `free-flat-rate`, aplique o template da aplicação com uma role de CloudFormation autorizada a criar também o WAF e a assinatura de preço fixo.

```bash
stack_output() {
  aws cloudformation describe-stacks --stack-name "$1" --region "$region" \
    --query "Stacks[0].Outputs[?OutputKey=='$2'].OutputValue | [0]" --output text
}

sam deploy --template-file infra/template.yaml \
  --stack-name "$stack" --region "$region" --capabilities CAPABILITY_NAMED_IAM \
  --role-arn "$(stack_output "$bootstrap" CloudFormationRoleArn)" \
  --s3-bucket "$(stack_output "$bootstrap" ArtifactBucket)" \
  --parameter-overrides CloudFrontMode=pay-as-you-go \
    "FrontendOriginAccessId=$(stack_output "$bootstrap" FrontendOriginAccessId)" \
    "ApiOriginAccessId=$(stack_output "$bootstrap" ApiOriginAccessId)" \
    "RuntimePermissionsBoundaryArn=$(stack_output "$bootstrap" RuntimePermissionsBoundaryArn)"
```

O bucket do SAM expira pacotes após 30 dias. Recompile a revisão desejada se precisar reimplantar artefatos expirados. Para publicar o frontend, gere o build de produção com API relativa em `/api` e `index.html` na raiz. O exemplo usa `frontend/dist`.

```bash
bucket="$(stack_output "$stack" FrontendBucket)"
url="$(stack_output "$stack" DemoUrl)"

aws s3 sync frontend/dist/assets/ "s3://$bucket/assets/" --region "$region" \
  --cache-control 'public,max-age=31536000,immutable'
aws s3 sync frontend/dist/ "s3://$bucket/" --region "$region" \
  --exclude index.html --exclude 'assets/*' --cache-control no-store
aws s3 cp frontend/dist/index.html "s3://$bucket/index.html" --region "$region" \
  --cache-control no-store --content-type 'text/html; charset=utf-8'

curl --fail-with-body --silent --show-error "$url/api/health"
curl --fail-with-body --silent --show-error "$url/" >/dev/null

origin="$(stack_output "$stack" ApiOriginUrl)"
status="$(curl -sS -o /dev/null -w '%{http_code}' "${origin%/}/api/health")"
test "$status" = 403
```

O frontend publica os assets com hash antes do HTML. Mantenha os assets da versão anterior até conferir o deploy. O teste valida que a rota responde via CloudFront e que a origem rejeita acesso direto sem assinatura. A Lambda autoriza `lambda:InvokeFunctionUrl` com `AWS_IAM` e `lambda:InvokeFunction` exclusivamente via Function URL, ambas limitadas ao ARN da distribuição.

Para operações com corpo, o navegador deve calcular seu SHA-256 e enviar o digest hexadecimal em `x-amz-content-sha256` junto aos mesmos bytes, inclusive um hash do corpo vazio em POST vazio. O CloudFront usa o digest para assinar a requisição à Lambda. A configuração usa as políticas gerenciadas `CachingDisabled` e `AllViewerExceptHostHeader` no comportamento da API.

## Verificar e operar

- Abra a URL, carregue um CSV fictício e confirme a divergência, o processamento e o resultado no painel. Repita com erro temporário e confira o histórico. Confirme também uma rota interna da interface carregada diretamente e um `404` de API preservado.
- Correlacione os registros nos grupos `/aws/lambda/<stack>-api` e `/aws/lambda/<stack>-worker`, com retenção de sete dias. A fila de falhas tem um alarme de profundidade. Consulte o estado do alarme e examine os registros de processamento antes de reprocessar itens.
- Antes de reimplantar uma versão anterior, confira sua compatibilidade com o formato persistido dos itens. Publique os dois artefatos da mesma revisão via SAM e o frontend daquela revisão. O identificador de cada stack permite distinguir demonstrações.
- Para encerrar a demonstração, revise os recursos no stack e esvazie o bucket de frontend antes de excluir o stack da aplicação. O DynamoDB contém dados fictícios e é removido junto com ele. Depois do término da remoção da distribuição, esvazie o bucket de pacotes antes de excluir o bootstrap.

## Referências

- [AWS Free Tier e planos da conta](https://docs.aws.amazon.com/awsaccountbilling/latest/aboutv2/free-tier-plans.html)
- [Elegibilidade do CloudFront Flat-Rate](https://docs.aws.amazon.com/PricingPlanManager/latest/UserGuide/plans.html)
- [Franquias provisionadas do DynamoDB](https://aws.amazon.com/dynamodb/pricing/)
- [OAC para Lambda Function URLs](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-restricting-access-to-lambda.html)
