/**
 * FAA Coded Instrument Flight Procedures (CIFP), cycle 2609: the Copter point-in-space extract (87N R190, KJRA R210,
 * KJFK R027, KLGA R250, 2P2 R029, with their reference records, terminal waypoints, the enroute fixes and navaids they
 * use, and their MSA records), unchanged from FAACIFP18. ARINC 424-18, a US Government work in the public domain.
 * Bundled into the client for the helicopter acceptance mission (heliDemo.ts). For demonstration only, not for
 * navigation: the cycle is not kept current.
 *
 * It is the same text as tests/fixtures/cifp/copter-pins-2609.pc, byte for byte (SHA-256 below); fms-87n-mission.spec.ts
 * keeps the two identical. A TypeScript string, not a ?raw import, so the logic tests can load it outside Vite.
 */
export const COPTER_PINS_CIFP_2609_SHA256 = "3cec10209b861f8abd2d6a004e4489b8a63c8b8c5656511c84acec5a76827c1a";

export const COPTER_PINS_CIFP_2609 = `HDR01FAACIFP18      001P013203968112609  12-AUG-202618:29:21  U.S.A. DOT FAA                                                FBB95AD8
HDR02                                 FEDERAL AVIATION ADMINISTRATION                                                               
HDR03                                 AERONAUTICAL INFORMATION SERVICES                                                             
SUSAD        CCC   K6011455VDLW N40554663W072475589    N40554663W072475589W0130000851     NARCALVERTON                     250582304
SUSAD        COL   K6011540VDLW N40184187W074093502    N40184188W074093502W0110001291     NARCOLTS NECK                    250942304
SUSAD        HTO   K6011360VTHW N40550839W072190014    N40550839W072190014W0130000222     NARHAMPTON                       253872012
SUSAEAENRT   BANKA K60    W   L N40225269W074030420                       W0124     NAR           BANKA                    285202504
SUSAEAENRT   BEADS K60    C   L N40440451W072323421                       W0131     NAR           BEADS                    286882605
SUSAEAENRT   ODALE K60    W     N40560429W073283603                       W0128     NAR           ODALE                    457822504
SUSAH 87N K6A87NH1   0     NARY N40504652W072275900W014000005         1800018000C    044044M SOUTHAMPTON                   758041810
SUSAH 87N K6CCRANN K60    W     N40514240W072275511                       W0132     NAR           CRANN                    758052504
SUSAH 87N K6CSTAYS K60    W     N40544214W072281168                       W0132     NAR           STAYS                    758062504
SUSAH 87N K6CTIDUE K60    W     N40574181W072282827                       W0132     NAR           TIDUE                    758072504
SUSAH 87N K6FR190  ACCC   010CCC  K6D 0V       IF                                             18000                 B PS   758082010
SUSAH 87N K6FR190  ACCC   020TIDUEK6HC0EY   010TF                                 + 01700                           B PS   758092010
SUSAH 87N K6FR190  ACCC   030TIDUEK6HC0EE AL   HF                     19000040    + 01700                           B PS   758102010
SUSAH 87N K6FR190  AHTO   010HTO  K6D 0V       IF                                             18000                 B PS   758112010
SUSAH 87N K6FR190  AHTO   020TIDUEK6HC0EY   010TF                                 + 01800                           B PS   758122010
SUSAH 87N K6FR190  AHTO   030TIDUEK6HC0EE AL   HF                     19000040    + 01700                           B PS   758132010
SUSAH 87N K6FR190  R      010TIDUEK6HC0E  I    IF                                 + 01700     18000070              B-PS   758142010
SUSAH 87N K6FR190  R      020STAYSK6HC1E  F 010TF                                 + 01700                 CRANN K6HCB PS   758152010
SUSAH 87N K6FR190  R      020STAYSK6HC2WN          N          ALNAV                                                   PS   758162010
SUSAH 87N K6FR190  R      030CRANNK6HC0EY M 031TF                                   00560          070 000          B-PS   758172010
SUSAH 87N K6FR190  R      040         0  M     CA                     1900        + 00439                           B PS   758182010
SUSAH 87N K6FR190  R      050BEADSK6EA0EY  R010DF                                 + 02000          070              B-PS   758192010
SUSAH 87N K6FR190  R      060BEADSK6EA0EE  R   HM                     23600040    + 02000          090              B-PS   758202010
SUSAH 87N K6SCRANNK6HC                0   18018001925                                                                  M   758211505
SUSAH KJRAK6AJRAH1   0     NARY N40451576W074002677W013000007         1800018000C    045045M WEST 30TH ST                  771562212
SUSAH KJRAK6CERORE K60    W     N40571735W073535724                       W0126     NAR           ERORE                    771572504
SUSAH KJRAK6CFEMDU K60    W     N41030971W073524689                       W0126     NAR           FEMDU                    771582504
SUSAH KJRAK6CJEDIL K60    W     N41000954W073524731                       W0126     NAR           JEDIL                    771592504
SUSAH KJRAK6CJORBA K60    W     N40531061W073553736                       W0125     NAR           JORBA                    771602605
SUSAH KJRAK6CWUDGO K60    W     N40591672W073485722                       W0126     NAR           WUDGO                    771612605
SUSAH KJRAK6CZABKI K60    W     N40540800W073551410                       W0125     NAR           ZABKI                    771622605
SUSAH KJRAK6FR210  AFEMDU 010FEMDUK6HC0E  A    IF                                             18000090              B-JH   771681807
SUSAH KJRAK6FR210  AFEMDU 020JEDILK6HC0EE B    TF                                 + 01700                           B JH   771691807
SUSAH KJRAK6FR210  AWUDGO 010WUDGOK6HC0E  A    IF                                             18000090              B-JH   771701807
SUSAH KJRAK6FR210  AWUDGO 020JEDILK6HC0EE B    TF                                 + 01700                           B JH   771711807
SUSAH KJRAK6FR210  R      010JEDILK6HC0E  I    IF                                 + 01700     18000090              B-JH   771721807
SUSAH KJRAK6FR210  R      020EROREK6HC0E  F 031TF                                 + 01500          070    JORBA K6HCB-JH   771731807
SUSAH KJRAK6FR210  R      021ZABKIK6HC0E S  031TF                                 + 00860                           B JH   771742009
SUSAH KJRAK6FR210  R      030JORBAK6HC0EY M 031TF                                   00780              000          B JH   771751807
SUSAH KJRAK6FR210  R      040         0  M     CA                     2101        + 00780          070              B-JH   771761807
SUSAH KJRAK6FR210  R      050JEDILK6HC0EY  L   DF                                 + 02000          090              B-JH   771771807
SUSAH KJRAK6FR210  R      060JEDILK6HC0EE  R   HM                     19310040    + 02000          090              B JH   771781807
SUSAH KJRAK6SJORBAK6HC                0   18018002925                                                                  M   771801807
SUSAP 2P2 K5A2P2     0     022YSN45231772W086552655W004000653         1800018000C    MNAR    WASHINGTON ISLAND             879851812
SUSAP 2P2 K5CHIREE K50    W     N45173254W087033378                       W0050     NAR           HIREE                    879862008
SUSAP 2P2 K5CIBUVE K50    W     N45171411W086592020                       W0051     NAR           IBUVE                    879872102
SUSAP 2P2 K5CJILIP K50    W     N45195755W086573342                       W0051     NAR           JILIP                    879882008
SUSAP 2P2 K5COBIBE K50    W     N45224099W086554656                       W0051     NAR           OBIBE                    879892504
SUSAP 2P2 K5FR029  AHIREE 010HIREEK5PC0E  A    IF                                             18000                 B PS   879931812
SUSAP 2P2 K5FR029  AHIREE 020IBUVEK5PC0EE B 010TF                     09990030    + 02500                           B PS   879941812
SUSAP 2P2 K5FR029  R      010IBUVEK5PC0E  I    IF                                 + 02500     18000                 B PS   879951812
SUSAP 2P2 K5FR029  R      020JILIPK5PC0E  F 010TF                     02870030    + 02000                 OBIBE K5PCB PS   879961812
SUSAP 2P2 K5FR029  R      030OBIBEK5PC0EY M 031TF                     02870030      01160              000          B PS   879971812
SUSAP 2P2 K5FR029  R      040         0  M     CA                     0287        + 01050                           B PS   879981812
SUSAP 2P2 K5FR029  R      050JILIPK5PC0EY  L010DF                                 + 02100                           B PS   879991812
SUSAP 2P2 K5FR029  R      060JILIPK5PC0EE  R   HM                     02870040    + 02100                           B PS   880001812
SUSAP 2P2 K5SOBIBEK5PC                0   18018002325                                                                  M   880051812
SUSAP KJFKK6AJFK     0     145YHN40382374W073464329W013000013         1800018000C    MNAR    JOHN F KENNEDY INTL           303921912
SUSAP KJFKK6CCOVIR K60    W     N40264664W074013590                       W0124     NAR           COVIR                    304062605
SUSAP KJFKK6CHELOG K60    W     N40324818W073593975                       W0125     NAR           HELOG                    304202504
SUSAP KJFKK6CWERIN K60    W     N40295324W074003599                       W0124     NAR           WERIN                    304492605
SUSAP KJFKK6FR027  ACOL   010COL  K6D 0V       IF                                             18000                 B PS   307541808
SUSAP KJFKK6FR027  ACOL   020BANKAK6EA0E  A 020TF                                 + 01800                           B PS   307551808
SUSAP KJFKK6FR027  ACOL   030COVIRK6PC0EE B 010TF                                 + 01800                           B PS   307561808
SUSAP KJFKK6FR027  R      010COVIRK6PC0E  I    IF                                 + 01800     18000070              B-PS   307571808
SUSAP KJFKK6FR027  R      020WERINK6PC0E  F 010TF                                 + 01800                 HELOG K6PCB PS   307581808
SUSAP KJFKK6FR027  R      030HELOGK6PC0EY M 031TF                                   00500              000          B PS   307591808
SUSAP KJFKK6FR027  R      040         0  M     CA                     0268        + 00433                           B PS   307601808
SUSAP KJFKK6FR027  R      050COVIRK6PC0EY  L010DF                                 + 01800          070              B-PS   307611808
SUSAP KJFKK6FR027  R      060COVIRK6PC0EE  L   HM                     02910040    + 01800                           B PS   307621808
SUSAP KJFKK6SHELOGK6PC                0   18018002925                                                                  M   308821513
SUSAP KLGAK6ALGA     0     070YHN40463807W073522138W012000021         1800018000C    MNAR    LAGUARDIA                     407262407
SUSAP KLGAK6CNEUMN K60    W     N40534800W073383000                       W0127     NAR           NEUMN                    407702504
SUSAP KLGAK6CWITKN K60    W     N40511200W073440000                       W0126     NAR           WITKN                    407882605
SUSAP KLGAK6CYORCI K60    W     N41002290W073355530                       W0127     NAR           YORCI                    407892605
SUSAP KLGAK6CZALAT K60    W     N40562569W073325367                       W0127     NAR           ZALAT                    407902605
SUSAP KLGAK6FR250  AODALE 010ODALEK6EA0E  A    IF                                             18000                 B JH   410860804
SUSAP KLGAK6FR250  AODALE 020ZALATK6PC0EE B 010TF                                 + 02000                           B JH   410871709
SUSAP KLGAK6FR250  AYORCI 010YORCIK6PC0E  A    IF                                             18000                 B JH   410881310
SUSAP KLGAK6FR250  AYORCI 020ZALATK6PC0EE B 010TF                                 + 02000                           B JH   410891709
SUSAP KLGAK6FR250  R      010ZALATK6PC0E  I    IF                                 + 02000     18000                 B JH   410901310
SUSAP KLGAK6FR250  R      020NEUMNK6PC0E  F 010TF                                 + 01200                 WITKN K6PCB JH   410911709
SUSAP KLGAK6FR250  R      030WITKNK6PC0EY M 030TF                                   00520          070 000          B-JH   410921709
SUSAP KLGAK6FR250  R      040         0  M     CA                     2501        + 00520                           B JH   410931709
SUSAP KLGAK6FR250  R      050         0    L   VAY                    0700        + 01240          070              B-JH   410941709
SUSAP KLGAK6FR250  R      060ZALATK6PC0EY      DF                                 + 02000                           B JH   410951310
SUSAP KLGAK6FR250  R      070ZALATK6PC0EE  L   HM                     07030040    + 02000          090              B JH   410961709
SUSAP KLGAK6SWITKNK6PC                0   18018002925                                                                  M   411611709
`;
