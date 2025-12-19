# Plano de Implementação: Suporte a Split de Pagamentos DePix

Este plano detalha as etapas para implementar o suporte aos parâmetros `depixSplitAddress` e `splitFee` na integração com a API da Eulen/DePix.

## 1. Análise da Arquitetura Atual

*   **Comunicação com API:** A classe `DepixService` gerencia as requisições HTTP para `https://depix.eulen.app/api/deposit`.
*   **Autenticação:** Utiliza Header `Authorization: Bearer <ApiKey>`.
*   **Fluxo de Depósito:** O método `RequestDepositAsync` constrói o payload JSON. Atualmente envia `amountInCents`, `depixAddress` e `whitelist`.
*   **Configuração:** Os dados são persistidos via `PixPaymentMethodConfig` e gerenciados pelo `PixController`.

## 2. Alterações Propostas

### 2.1. Atualização do Modelo de Dados
*   **Arquivo:** `BTCPayServer.Plugins.Depix/PaymentHandlers/PixPaymentMethodConfig.cs`
*   **Ação:** Adicionar propriedades opcionais:
    ```csharp
    public string? DepixSplitAddress { get; set; }
    public string? SplitFee { get; set; }
    ```

### 2.2. Atualização da Interface de Configuração (UI)
*   **ViewModel:** `BTCPayServer.Plugins.Depix/Data/Models/ViewModels/PixStoreViewModel.cs`
    *   Adicionar campos `DepixSplitAddress` e `SplitFee`.
*   **View:** `BTCPayServer.Plugins.Depix/Views/Pix/PixSettings.cshtml`
    *   Adicionar inputs de texto para os novos campos na página de configurações do plugin.
*   **Controller:** `BTCPayServer.Plugins.Depix/Controllers/PixController.cs`
    *   Mapear os dados entre o ViewModel e o Config no `GET` e `POST`.

### 2.3. Atualização da Lógica de Serviço
*   **Arquivo:** `BTCPayServer.Plugins.Depix/Services/DepixService.cs`
*   **Ação:** Atualizar o método `RequestDepositAsync` para aceitar `depixSplitAddress` e `splitFee`.
    *   Incluir estes campos no payload JSON apenas se não forem nulos ou vazios.
*   **Arquivo:** `BTCPayServer.Plugins.Depix/PaymentHandlers/PixPaymentMethodHandler.cs`
    *   Atualizar o método `ConfigurePrompt` para ler as configurações e passar os novos valores para o `DepixService`.

## 3. Requisitos Técnicos e Validação
*   **Compatibilidade:** Os novos campos serão opcionais, garantindo que instalações existentes continuem funcionando sem reconfiguração obrigatória.
*   **Tratamento de Erros:** A API já retorna erros (não 200) que são capturados pelo `DepixService`. Erros específicos de formato nos novos campos resultarão em falha na geração do QR Code, que será logada.

## 4. Passos de Execução
1.  Modificar `PixPaymentMethodConfig.cs`.
2.  Modificar `PixStoreViewModel.cs`.
3.  Atualizar `PixSettings.cshtml`.
4.  Atualizar `PixController.cs`.
5.  Atualizar `DepixService.cs`.
6.  Atualizar `PixPaymentMethodHandler.cs`.
