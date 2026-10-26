# Protocolo dos LEDs da BIOSTAR B450GT (engenharia reversa do RACING GT EVO 3.24)

Fonte: análise estática de `RCGTII.exe` (SHA-256 `c11aee77…cfeec`) e `BS_RVSIO64.sys`
(SHA-256 `a8aae612…badb7`), mais leituras/varredura **somente leitura** na placa.
**Nenhuma escrita foi testada no hardware ainda.**

Legenda: ✅ observado no código · 🔬 confirmado no hardware (leitura) · 🟡 inferido · ❓ desconhecido

## 1. Onde fica o controlador

Cada chave de LED da placa (`LED_SYS`, `LED_H1`, `LED_H2`, `LED_IO`) com valor ≠ 0 faz o app tentar
**dois controladores em sequência**: ✅

| Chave | 1ª tentativa | 2ª tentativa (se a 1ª não for detectada) |
|---|---|---|
| `LED_SYS` | MCU `LED_IIC` (padrão `0xB0` → 7 bits `0x58`), `LEDMCU_SMB` (padrão 2) | `LEDMCU1`: `LED_IIC1` (padrão `0x60` → **`0x30`**), `LEDMCU1_SMB` (padrão 1) |
| `LED_H1` | idem | `LEDMCU2`: `LED_IIC2` (padrão `0x60` → **`0x30`**), `LEDMCU2_SMB` (padrão 1) |
| `LED_H2` | idem | `LEDMCU3`: `LED_IIC3`, `LEDMCU3_SMB` |
| `LED_IO` | idem | `LEDMCUD`: `LED_IIC4` (padrão `0x60` → **`0x30`**), `LEDMCUD_SMB` (padrão 1) |

Na B450GT (sem chaves `LED_IIC*` no `.ini`): `LED_SYS=0`, `LED_H1=2` (12V RGB), `LED_IO=4` (5V ARGB).

Hardware (varredura somente leitura, "receive byte"): 🔬

| Barramento | `0x58` | `0x30` | `0x4F` |
|---|---|---|---|
| PawnIO PIIX4 porta 0 (principal `0x0B00`) | não responde | — | — |
| PawnIO PIIX4 porta 1 (auxiliar `0x0B20`) | não responde | **responde** (byte `0x80`) | responde (byte `0x00`, função ❓) |

Conclusão: na B450GT os LEDs da placa ficam no **chip `0x30` do SMBus auxiliar `0x0B20`** 🔬

Leitura dos registradores em `0x30` (2026-10-26, somente leitura): 🔬

| Registrador | Valor | Interpretação |
|---|---|---|
| `0x97` | `0x07` | 12V ligado (bit 1); bits 0 e 2 também ativos (❓) |
| `0x9C`/`0x9D`/`0x9E` | `0x66`/`0x49`/`0xFF` | 12V cor #6649FF |
| `0x9F` | `0x81` | 12V modo estático (código 1) |
| `0xA4` | `0x00` | 5V "aplicar" ocioso |
| `0xA5` | `0x80` | 5V ligado (bit 7) |
| `0xA6` | `0x89` | 5V modo 9 (= "estático" na UI) |
| `0xA7`/`0xA8`/`0xA9` | `0x66`/`0x49`/`0xFF` | 5V cor #6649FF |

Os dois conectores com a mesma cor e modo estático são coerentes com uma configuração sincronizada.
Conferido visualmente: os LEDs mostram lilás, compatível com #6649FF. 🔬

Testes de escrita (2026-10-26, com restauração automática dos bytes originais): 🔬

| Teste | Lido de volta | Efeito visível |
|---|---|---|
| 12V: cor `0x9C`–`0x9E` = vermelho (sem nada mais) | `#FF0000` | nenhum |
| 12V: cor = vermelho + regrava `0x9F` = `0x81` | `#FF0000` | nenhum |
| **5V: cor `0xA7`–`0xA9` = verde + `0xA4` = `0x01`** | `#00FF00` | **LEDs ficaram verdes** ✔ |

Conclusão: as luzes desta máquina estão no **conector 5V (ARGB)**; a sequência cor + "aplicar" funciona.
O 12V aceita as escritas, mas não há nada visível ligado nele (ou falta outro gatilho ❓).

## 2. Registradores — 12V RGB (`LED_H1`, classe `LEDMCU2`, métodos `0x4ce558…0x4ce8d8`)

| Função | Registrador | Status |
|---|---|---|
| Liga/desliga (ler-modificar-escrever) | `0x97` bit 1 (`0x02`) | ✅ |
| Modo | `0x9F` = `(atual & 0x10) \| 0x80` + código | ✅ |
| Cor R / G / B | `0x9C` / `0x9D` / `0x9E` | ✅ |

Códigos de modo (modo da UI → código): 7→`4`, 8→`2`, 9→`1`, 10→`8`; demais passam direto. ✅
Pela classe das memórias: 9 = estático, 7 = respiração, 8 = piscar. 🟡
Ex.: estático = `0x81`, respiração = `0x84`, piscar = `0x82` (bit 4 preservado). 🟡

## 3. Registradores — 5V ARGB (`LED_IO`, classe `LEDMCUD`, métodos `0x4cffb0…0x4d0368`)

| Função | Registrador | Status |
|---|---|---|
| Liga/desliga (ler-modificar-escrever) | `0xA5` bit 7 (`0x80`) | ✅ |
| Modo | `0xA6` = `0x80` + modo (sem tradução) | ✅ |
| Cor R / G / B | `0xA7` / `0xA8` / `0xA9` | ✅ |
| Aplicar | `0xA4` = `0x01` após cada alteração | ✅ |
| Velocidade do efeito | `0xAB`, 1–255 | ✅ valor menor = mais rápido (conferido no Arco-íris 🔬) |
| Brilho | `0xAA`, 1–16 | ✅ (não usado no app ainda) |

Velocidade e brilho saem do slot 10 da vtable do 5V (`0x4d099c`): `(a, b, aplicar)` grava `a` em `0xAB`
se `a ≠ 0x5050`, senão `b` em `0xAA`; depois `0xA4` = `0x01` se `aplicar`. ✅
- Slider de velocidade (`Img_P4_S_Speed_BMouseMove`, `0x436220`): `0xAB = 255 − round(pos/104 × 255)`,
  0 vira 1; valor guardado por efeito na chave `3_Speed` (padrão 50). ✅
- Slider de brilho (`Img_P4_C_Light_B*`, `0x43a6a8`/`0x43a96c`): valor limitado a 1–16 e gravado em `0xAA`. ✅
- A classe do 12V (`LEDMCU2`) não tem método equivalente (slot 10 sem escrita). ✅

Modos do 5V (`0xA6` = `0x80 | código`, depois `0xA4` = `0x01`). Códigos tirados dos handlers de clique
`Img_P4_S_xx_*_2Click`, que gravam o código em `[obj+0x85]`: ✅ Os 9 efeitos usados no app (todos menos
Music) foram aplicados pelo Racing Studio e conferidos visualmente: cada um corresponde ao nome. 🔬

| Botão no RACING GT EVO | Código | `0xA6` |
|---|---|---|
| 01 Permanent (estático) | 9 | `0x89` (valor lido na placa 🔬) |
| 02 Breathe (respiração) | 7 | `0x87` |
| 03 Shine (piscar) | 8 | `0x88` |
| 04 Music | 10 | `0x8A` (depende do áudio; não usado no app) |
| 05 Meteor | 1 | `0x81` |
| 06 Lightning | 2 | `0x82` |
| 07 Wave | 3 | `0x83` |
| 08 Rainbow | 5 | `0x85` |
| 09 Starrysky | 4 | `0x84` |
| 10 Aurora | 6 | `0x86` |

Os botões da página 12V (`02_2 Breathe`=10, `04_2 Music`=25, `11 CPermanent`=23, `12 CBreathe`=24,
`13 CShine`=11, `14 StarryNight`=11) usam os códigos que a tabela de tradução do 12V converte (seção 2). ✅

## 4. `LED_SYS` (`LEDMCU1`) — não usado na B450GT

Liga/desliga `0x97` bit 0; modo `0x9B` (mesma tradução do 12V); cor `0x98`/`0x99`/`0x9A`; `0xAF` ❓. ✅

## 5. Memórias RGB ("Memory Sync") — protocolo ENE, separado da placa

Endereços `0x74`–`0x76` (varredura em `0x70`–`0x77`). Registrador selecionado por *word* no
comando `0x00` (bytes invertidos), byte no comando `0x01`, bloco no comando `0x03`:
cores em `0x8010` (R,B,G × 5), aplicar em `0x80A0` = `0x01`, modo em `0x8020` (16 bytes). ✅

## 6. Detalhes gerais

- Cores vêm como `TColor` (`0x00BBGGRR`): byte 0 → R, 1 → G, 2 → B. ✅
- Uma flag global (`0x77cf70`) faz o app ler alguns registradores como *word* (`0x96`, `0x9E`, `0xA4`)
  em vez de *byte*; o efeito é o mesmo para o byte de interesse. ✅ (motivo ❓)
- Todos os acessos usam "read/write byte data" (comando = registrador). ✅

## 7. Cuidados para a implementação

- Antes de escrever, **ler** `0x97`, `0x9C`–`0x9F`, `0xA4`–`0xA9` em `0x30` e conferir com a cor atual.
- Sempre ler-modificar-escrever `0x97` e `0xA5`; preservar o bit 4 de `0x9F`.
- Não escrever em `0x4F` (função desconhecida) nem varrer o barramento principal (SPD dos pentes,
  regulador de tensão `uP1816` em `0x2A`).
