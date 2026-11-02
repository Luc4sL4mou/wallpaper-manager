# Política de segurança

## Reportando uma vulnerabilidade

Se você encontrou uma falha de segurança no Wallpaper Manager, **não abra uma issue pública**.
Use o recurso "Report a vulnerability" na aba *Security* deste repositório no GitHub
(ou entre em contato diretamente com o mantenedor) descrevendo o problema e como reproduzi-lo.

## Boas práticas já adotadas

- A chave de API do Wallhaven é guardada criptografada (DPAPI, escopo do usuário atual) e enviada por cabeçalho, nunca pela URL.
- Downloads só são aceitos por HTTPS, do domínio `wallhaven.cc`, com extensão de imagem permitida e limite de tamanho.
- O app não executa código baixado e não pede privilégios de administrador.
