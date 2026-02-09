// Steam Value Calculator - SignalR Version
// Versão mais robusta usando SignalR ao invés de SSE

// Importar biblioteca SignalR
// Adicione no head do HTML: <script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>

document.addEventListener('DOMContentLoaded', function () {
    const form = document.getElementById('calculatorForm');
    const btnCalculate = document.getElementById('btnCalculate');
    const progressContainer = document.getElementById('progressContainer');
    const progressBar = document.getElementById('progressBar');
    const progressPercentage = document.getElementById('progressPercentage');
    const statusMessage = document.getElementById('statusMessage');
    const resultsContainer = document.getElementById('resultsContainer');
    const resultsContent = document.getElementById('resultsContent');
    const errorContainer = document.getElementById('errorContainer');

    let currentProgress = 0;
    let totalValue = 0;
    let connection = null;

    // Configurar conexão SignalR
    function setupSignalR() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl("/calculationHub")
            .withAutomaticReconnect()
            .configureLogging(signalR.LogLevel.Information)
            .build();

        // Event Handlers
        connection.on("UpdateProgress", (progress, message) => {
            updateProgress(progress, message);
        });

        connection.on("ReceiveGamesData", (games, total) => {
            addGamesSection(games, total);
        });

        connection.on("ReceiveInventoryData", (game, items, total) => {
            addInventorySection(game, items, total);
        });

        connection.on("ReceiveTotalValue", (total) => {
            addTotalSection(total);
        });

        connection.on("ReceiveError", (message) => {
            showError(message);
            resetForm();
        });

        // Reconexão automática
        connection.onreconnecting(() => {
            console.log("Reconectando...");
            updateProgress(currentProgress, "Reconectando...");
        });

        connection.onreconnected(() => {
            console.log("Reconectado!");
        });

        connection.onclose(() => {
            console.log("Conexão fechada");
        });

        // Iniciar conexão
        return connection.start()
            .then(() => {
                console.log("SignalR conectado!");
            })
            .catch(err => {
                console.error("Erro ao conectar SignalR:", err);
                showError("Erro ao conectar com o servidor. Recarregue a página.");
            });
    }

    // Inicializar SignalR ao carregar a página
    setupSignalR();

    // Event Listener do formulário
    form.addEventListener('submit', async (e) => {
        e.preventDefault();

        // Verificar se está conectado
        if (connection.state !== signalR.HubConnectionState.Connected) {
            showError("Aguarde, conectando ao servidor...");
            await setupSignalR();
        }

        // Reset
        progressContainer.classList.add('active');
        resultsContainer.classList.remove('active');
        resultsContent.innerHTML = '';
        errorContainer.innerHTML = '';
        totalValue = 0;
        currentProgress = 0;

        btnCalculate.disabled = true;
        btnCalculate.innerHTML = '<span>Calculando...</span>';

        const profileUrl = document.getElementById('profileUrl').value;
        const calculateGames = document.getElementById('calculateGames').checked;
        const calculateInventory = document.getElementById('calculateInventory').checked;

        try {
            // Invocar método do Hub
            await connection.invoke("StartCalculation", profileUrl, calculateGames, calculateInventory);

            // Aguardar conclusão (será tratado pelos eventos)
            setTimeout(() => {
                if (currentProgress >= 100) {
                    resetForm();
                }
            }, 2000);

        } catch (error) {
            console.error('Error:', error);
            showError('Erro ao calcular valores. Verifique o link do perfil e tente novamente.');
            resetForm();
        }
    });

    function updateProgress(progress, message) {
        currentProgress = progress;
        progressBar.style.width = progress + '%';
        progressPercentage.textContent = Math.round(progress) + '%';
        statusMessage.innerHTML = message + '<span class="loading-dots"><span>.</span><span>.</span><span>.</span></span>';

        // Auto-esconder após conclusão
        if (progress >= 100) {
            setTimeout(() => {
                progressContainer.classList.remove('active');
            }, 1500);
        }
    }

    function addGamesSection(items, total) {
        resultsContainer.classList.add('active');

        const section = document.createElement('div');
        section.className = 'results-section';
        section.innerHTML = `
            <div class="results-header">
                <span class="results-icon">🎮</span>
                <h2 class="results-title">Jogos</h2>
            </div>
            <div class="item-list" id="gamesList"></div>
        `;

        resultsContent.appendChild(section);
        const gamesList = section.querySelector('#gamesList');

        items.forEach((item, index) => {
            setTimeout(() => {
                const itemEl = document.createElement('div');
                itemEl.className = 'item';
                itemEl.innerHTML = `
                    <div style="display:flex;align-items:center;gap:12px">
                        <img src="${escapeHtml(item.imageUrl || ('https://cdn.akamai.steamstatic.com/steam/apps/' + item.appId + '/header.jpg'))}" alt="${escapeHtml(item.name)}" style="width:64px;height:36px;object-fit:cover;border-radius:6px;border:1px solid rgba(255,255,255,0.04)"/>
                        <div>
                            <div class="item-name">${escapeHtml(item.name)}</div>
                            <div class="item-value">R$ ${Number(item.value).toFixed(2)}</div>
                        </div>
                    </div>
                `;
                gamesList.appendChild(itemEl);
            }, index * 50);
        });

        totalValue += total;
    }

    function addInventorySection(game, items, total) {
        resultsContainer.classList.add('active');

        const icons = {
            'CS2': '🔫',
            'Dota 2': '⚔️',
            'TF2': '🎩'
        };

        const section = document.createElement('div');
        section.className = 'results-section';
        section.innerHTML = `
            <div class="results-header">
                <span class="results-icon">${icons[game] || '📦'}</span>
                <h2 class="results-title">${escapeHtml(game)}</h2>
            </div>
            <div class="item-list" id="inventory${game.replace(/\s/g, '')}"></div>
        `;

        resultsContent.appendChild(section);
        const inventoryList = section.querySelector(`#inventory${game.replace(/\s/g, '')}`);

        items.forEach((item, index) => {
            setTimeout(() => {
                const itemEl = document.createElement('div');
                itemEl.className = 'item';
                itemEl.innerHTML = `
                    <span class="item-name">${escapeHtml(item.name)}</span>
                    <span class="item-value">R$ ${Number(item.value).toFixed(2)}</span>
                `;
                inventoryList.appendChild(itemEl);
            }, index * 50);
        });

        totalValue += total;
    }

    function addTotalSection(total) {
        const totalSection = document.createElement('div');
        totalSection.className = 'total-value';
        totalSection.innerHTML = `
            <div class="total-label">💰 Valor Total da Conta</div>
            <div class="total-amount">R$ ${Number(total).toFixed(2)}</div>
        `;
        resultsContent.appendChild(totalSection);
    }

    function showError(message) {
        errorContainer.innerHTML = `
            <div class="error-message">
                <strong>❌ Erro:</strong> ${escapeHtml(message)}
            </div>
        `;
    }

    function resetForm() {
        btnCalculate.disabled = false;
        btnCalculate.innerHTML = '<span>Calcular Novamente</span>';
        progressContainer.classList.remove('active');
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
});