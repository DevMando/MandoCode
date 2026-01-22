const playerTurn = 'X';
const gameBoard = ['', '', '', '', '', '', '', '', ''];

function handleClick(event) {
    const cellIndex = event.target.getAttribute('data-index');
    if (gameBoard[cellIndex] === '') {
        gameBoard[cellIndex] = playerTurn;
        document.getElementById(`cell-${cellIndex}`).innerText = playerTurn;
        playerTurn = playerTurn === 'X' ? 'O' : 'X';
    }
}