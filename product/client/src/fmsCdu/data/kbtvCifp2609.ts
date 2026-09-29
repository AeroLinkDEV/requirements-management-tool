/**
 * FAA Coded Instrument Flight Procedures (CIFP), cycle 2609: the Burlington, Vermont (KBTV) extract, unchanged from
 * FAACIFP18. ARINC 424-18, a US Government work in the public domain. Bundled into the client so the FMS Test Bench can
 * load a real approach with one action (kbtvDemo.ts). For demonstration only, not for navigation: the cycle is not kept
 * current.
 *
 * It is the same text as tests/fixtures/cifp/kbtv-2609.pc, byte for byte (SHA-256 below); fms-kbtv-demo.spec.ts keeps
 * the two identical. It is a TypeScript string, not a ?raw import, so the logic tests, which run outside Vite, can load
 * the modules that use it.
 */
export const KBTV_CIFP_2609_SHA256 = "48f1d4f97a9a2ed24cab3c28a533e4af4f5d9f4af702f0ccfc0e2ffc9d5cb177";

export const KBTV_CIFP_2609 = `HDR01FAACIFP18      001P013203968112609  12-AUG-202618:29:21  U.S.A. DOT FAA                                                FBB95AD8
SUSAD        BTV   K6011750VDLW N44234958W073105749    N44234958W073105749W0150004171     NARBURLINGTON                    250341911
SUSAD KBTVK6 IBTV  K6011030 ITW                    IBTVN44275134W073082691W0150003420     NARPATRICK LEAHY BURLINGTON      260812310
SUSAD KBTVK6 IVOE  K6011030 ITWN                   IVOEN44275134W073082691W0150003420     NARPATRICK LEAHY BURLINGTON      260822310
SUSAEAENRT   WINLA K60    C   L N44173158W073120666                       W0135     NAR           WINLA                    537522605
SUSAP KBTVK6ABTV     0     083YHN44281904W073091179W015000335         1800018000C    MNAR    PATRICK LEAHY BURLINGTON INTL 412312310
SUSAP KBTVK6CCESAL K60    W     N44230754W073080426                       W0136     NAR           CESAL                    412322504
SUSAP KBTVK6CCREME K60    W     N44113997W072473820                       W0137     NAR           CREME                    412332601
SUSAP KBTVK6CDONGY K60    W     N44084659W072544278                       W0136     NAR           DONGY                    412342605
SUSAP KBTVK6CEHIKO K60    C     N44212566W072580032                       W0136     NAR           EHIKO                    412352605
SUSAP KBTVK6CFOBUX K60    W     N44152474W073062733                       W0135     NAR           FOBUX                    412362605
SUSAP KBTVK6CFOGMO K60    W     N44222522W072530099                       W0137     NAR           FOGMO                    412372601
SUSAP KBTVK6CFOVES K60    C     N44321266W073152483                       W0135     NAR           FOVES                    412382605
SUSAP KBTVK6CHABOK K60    W     N44274059W073080427                       W0136     NAR           HABOK                    412392605
SUSAP KBTVK6CHONIB K60    C     N44193023W072545520                       W0136     NAR           HONIB                    412402605
SUSAP KBTVK6CIRAGE K60    W     N44264714W073053123                       W0136     NAR           IRAGE                    412412605
SUSAP KBTVK6CJANUD K60    C     N44144870W072472479                       W0137     NAR           JANUD                    412422605
SUSAP KBTVK6CJENAX K60    W     N44142182W073160506                       W0135     NAR           JENAX                    412432601
SUSAP KBTVK6CJIDSO K60    C     N44230980W073004771                       W0136     NAR           JIDSO                    412442605
SUSAP KBTVK6CJRVIS K60    C     N44385395W073025100                       W0137     NAR           JRVIS                    412452504
SUSAP KBTVK6CJUNEL K60    W     N44301276W073121043                       W0136     NAR           JUNEL                    412462504
SUSAP KBTVK6CKOTDE K60    R     N44251699W073041253                       W0136     NAR           KOTDE                    412472605
SUSAP KBTVK6CMANIF K60    W     N44293821W073134365                       W0135     NAR           MANIF                    412482605
SUSAP KBTVK6CNASEE K60    W     N44271427W073064887                       W0136     NAR           NASEE                    412492605
SUSAP KBTVK6CNIDUQ K60    C     N44180358W072523624                       W0136     NAR           NIDUQ                    412502605
SUSAP KBTVK6COBRIE K60    W     N44254344W073083702                       W0136     NAR           OBRIE                    412512601
SUSAP KBTVK6COTYIJ K60    R     N44272985W073085942                       W0136     NAR           OTYIJ                    412522504
SUSAP KBTVK6CSILDE K60    W     N44185191W072424689                       W0137     NAR           SILDE                    412532605
SUSAP KBTVK6CSTAEV K60    C     N44362503W073221521                       W0135     NAR           STAEV                    412542605
SUSAP KBTVK6CVAMPY K60    W     N44205026W072400523                       W0137     NAR           VAMPY                    412552605
SUSAP KBTVK6CWENKI K60    W     N44254998W073024785                       W0136     NAR           WENKI                    412562605
SUSAP KBTVK6CWULEB K60    W     N44422887W073145661                       W0136     NAR           WULEB                    412572605
SUSAP KBTVK6CYUNUD K60    W     N44302072W073293229                       W0134     NAR           YUNUD                    412582605
SUSAP KBTVK6CZABOX K60    W     N44234258W072564465                       W0136     NAR           ZABOX                    412592605
SUSAP KBTVK6FI15   ASTAEV 010STAEVK6PC0EE AR   HF IBTVK6      325801311458T010PI  + 03300     18000                 0 NS   412602601
SUSAP KBTVK6FI15   AWULEB 010WULEBK6PC0E  A    IF                                             18000                 0 PS   412612301
SUSAP KBTVK6FI15   AWULEB 020STAEVK6PC0EE B 010TF IBTVK6      32580131        PI  + 03300                           0 PS   412622601
SUSAP KBTVK6FI15   AYUNUD 010YUNUDK6PC0E  A    IF                                             18000                 0 PS   412632301
SUSAP KBTVK6FI15   AYUNUD 020STAEVK6PC0EE B 010TF IBTVK6      32580131        PI  + 03300                           0 PS   412642601
SUSAP KBTVK6FI15   I      010STAEVK6PC0E  I    IF IBTVK6      32580131        PI  J 033000200018000                 0 NS   412652601
SUSAP KBTVK6FI15   I      020FOVESK6PC0E  F    CF IBTVK6      3258006614600065PI  H 0200002000                      0 NS   412662411
SUSAP KBTVK6FI15   I      030RW15 K6PG0GY M    CF IBTVK6      3258001514600052PI    00357             -300          0 NS   412672411
SUSAP KBTVK6FI15   I      040         0  M     CA                     1458        + 00800                           0 NS   412682411
SUSAP KBTVK6FI15   I      050BTV  K6D 0V   R   DF                                                                   0 NS   412692411
SUSAP KBTVK6FI15   I      060WINLAK6EA0EY      CF BTV K6      2025006420250064D   + 03600                           0 NS   412702411
SUSAP KBTVK6FI15   I      070WINLAK6EA0EE  L   HM                     0225T010    + 03600                           0 NS   412712411
SUSAP KBTVK6FI33   ADONGY 010DONGYK6PC0E  A    IF                                             18000                 0 PS   412722601
SUSAP KBTVK6FI33   ADONGY 020JANUDK6PC0EE B 010TF IVOEK6      14580216        PI  + 07000                           0 PS   412732601
SUSAP KBTVK6FI33   AJANUD 010JANUDK6PC0EE AR   HF IVOEK6      145802163258T010PI  + 07000     18000                 0 NS   412742601
SUSAP KBTVK6FI33   AVAMPY 010VAMPYK6PC0E       IF                                             18000                 0 PS   412752601
SUSAP KBTVK6FI33   AVAMPY 020JANUDK6PC0EE   010TF IVOEK6      14580216        PI  + 07000                           0 PS   412762601
SUSAP KBTVK6FI33   I      010JANUDK6PC0E  I    IF IVOEK6      14580216        PI  J 070000380018000                 0 NS   412772111
SUSAP KBTVK6FI33   I      011NIDUQK6PC0E       CF IVOEK6      1458016632600050PI  + 05400                           0 NS   412782411
SUSAP KBTVK6FI33   I      012HONIBK6PC0E       CF IVOEK6      1458014432600022PI    04700                           0 NS   412792601
SUSAP KBTVK6FI33   I      020EHIKOK6PC0E  F    CF IVOEK6      1458011532600029PI  H 0380003800                      0 NS   412802601
SUSAP KBTVK6FI33   I      030RW33 K6PG0GY M    CF IVOEK6      1458001532600100PI    00389             -320          0 NS   412812411
SUSAP KBTVK6FI33   I      040         0  M     CA                     3258        + 01400                           0 NS   412822601
SUSAP KBTVK6FI33   I      050BTV  K6D 0VY      DF                                 + 03100                           0 NS   412832601
SUSAP KBTVK6FI33   I      060BTV  K6D 0VE  L   HM                     0360T010    + 03100                           0 NS   412842601
SUSAP KBTVK6FL15   ASTAEV 010STAEVK6PC0EE AR   HF IBTVK6      325801311458T010PI  + 03300     18000                 0 NS   412852601
SUSAP KBTVK6FL15   AWULEB 010WULEBK6PC0E  A    IF                                             18000                 0 PS   412862301
SUSAP KBTVK6FL15   AWULEB 020STAEVK6PC0EE B 010TF IBTVK6      32580131        PI  + 03300                           0 PS   412872601
SUSAP KBTVK6FL15   AYUNUD 010YUNUDK6PC0E  A    IF                                             18000                 0 PS   412882301
SUSAP KBTVK6FL15   AYUNUD 020STAEVK6PC0EE B 010TF IBTVK6      32580131        PI  + 03300                           0 PS   412892601
SUSAP KBTVK6FL15   L      010STAEVK6PC0E  I    IF IBTVK6      32580131        PI  + 03300     18000                 0 NS   412902601
SUSAP KBTVK6FL15   L      020FOVESK6PC0E  F    CF IBTVK6      3258006614600065PI  + 02000                           0 NS   412912411
SUSAP KBTVK6FL15   L      030RW15 K6PG0GY M    CF IBTVK6      3258001514600052PI    00357             -300          0 NS   412922411
SUSAP KBTVK6FL15   L      040         0  M     CA                     1458        + 00800                           0 NS   412932411
SUSAP KBTVK6FL15   L      050BTV  K6D 0V   R   DF                                                                   0 NS   412942411
SUSAP KBTVK6FL15   L      060WINLAK6EA0EY      CF BTV K6      2025006420250064D   + 03600                           0 NS   412952411
SUSAP KBTVK6FL15   L      070WINLAK6EA0EE  L   HM                     0225T010    + 03600                           0 NS   412962411
SUSAP KBTVK6FL33   ADONGY 010DONGYK6PC0E  A    IF                                             18000                 0 PS   412972601
SUSAP KBTVK6FL33   ADONGY 020JANUDK6PC0EE B 010TF IVOEK6      14580216        PI  + 07000                           0 PS   412982601
SUSAP KBTVK6FL33   AJANUD 010JANUDK6PC0EE AR   HF IVOEK6      145802163258T010PI  + 07000     18000                 0 NS   412992601
SUSAP KBTVK6FL33   AVAMPY 010VAMPYK6PC0E       IF                                             18000                 0 PS   413002601
SUSAP KBTVK6FL33   AVAMPY 020JANUDK6PC0EE   010TF IVOEK6      14580216        PI  + 07000                           0 PS   413012601
SUSAP KBTVK6FL33   L      010JANUDK6PC0E  I    IF IVOEK6      14580216        PI  + 07000     18000                 0 NS   413022411
SUSAP KBTVK6FL33   L      011NIDUQK6PC0E       CF IVOEK6      1458016632600050PI  + 05400                           0 NS   413032411
SUSAP KBTVK6FL33   L      012HONIBK6PC0E       CF IVOEK6      1458014432600022PI  + 04700                           0 NS   413042411
SUSAP KBTVK6FL33   L      020EHIKOK6PC0E  F    CF IVOEK6      1458011532600029PI  + 03800                           0 NS   413052601
SUSAP KBTVK6FL33   L      021JIDSOK6PC0E S     CF IVOEK6      1458008832600027PI  + 02900             -320          0 NS   413062411
SUSAP KBTVK6FL33   L      022KOTDEK6PC0E S     CF IVOEK6      1458005632600032PI  + 01800             -320          0 NS   413072411
SUSAP KBTVK6FL33   L      030RW33 K6PG0GY M    CF IVOEK6      1458001532600042PI    00389             -320          0 NS   413082411
SUSAP KBTVK6FL33   L      040         0  M     CA                     3258        + 01400                           0 NS   413092601
SUSAP KBTVK6FL33   L      050BTV  K6D 0VY  L   DF                                 + 03000                           0 NS   413102411
SUSAP KBTVK6FL33   L      060BTV  K6D 0VE  L   HM                     0360T010    + 03000                           0 NS   413112411
SUSAP KBTVK6FR01   AFOBUX 010FOBUXK6PC0EE AL   HF                     00650050    + 04800     18000                 A JS   413122205
SUSAP KBTVK6FR01   AJENAX 010JENAXK6PC0E  A    IF                                             18000                 A JS   413132205
SUSAP KBTVK6FR01   AJENAX 020FOBUXK6PC0EE B 010TF                                 + 04800                           A JS   413142205
SUSAP KBTVK6FR01   R      010FOBUXK6PC0E  I    IF                                 + 04800     18000                 A JS   413152205
SUSAP KBTVK6FR01   R      020CESALK6PC1E  F 010TF                                 + 02400                           A JS   413162205
SUSAP KBTVK6FR01   R      020CESALK6PC2WALPV       N          ALNAV                                                   JS   413172205
SUSAP KBTVK6FR01   R      021OBRIEK6PC0E S  031TF                                 V 0128001283        -400          A JS   413182601
SUSAP KBTVK6FR01   R      030RW01 K6PG0GY M 031TF                                   00363             -400          A JS   413192205
SUSAP KBTVK6FR01   R      040         0  M     CA                     0064        + 00900                           A JS   413202205
SUSAP KBTVK6FR01   R      050JRVISK6PC0EY  R010DF                                 + 03500                           A JS   413212205
SUSAP KBTVK6FR01   R      060JRVISK6PC0EE  R   HM                     21600040    + 03500                           A JS   413222205
SUSAP KBTVK6FR15   ASTAEV 010STAEVK6PC0EE AR   HF                     14570040    + 03200     18000                 A JS   413231505
SUSAP KBTVK6FR15   AWULEB 010WULEBK6PC0E  A    IF                                             18000                 A JS   413241505
SUSAP KBTVK6FR15   AWULEB 020STAEVK6PC0EE B 010TF                                 + 03200                           A JS   413252110
SUSAP KBTVK6FR15   AYUNUD 010YUNUDK6PC0E  A    IF                                             18000                 A JS   413261505
SUSAP KBTVK6FR15   AYUNUD 020STAEVK6PC0EE B 010TF                                 + 03200                           A JS   413272110
SUSAP KBTVK6FR15   R      010STAEVK6PC0E  I    IF                                 + 03200     18000                 A JS   413281505
SUSAP KBTVK6FR15   R      020FOVESK6PC1E  F 010TF                                 + 02000                           A JS   413292110
SUSAP KBTVK6FR15   R      020FOVESK6PC2WALPV       ALNAV/VNAV ALNAV                                                   JS   413301505
SUSAP KBTVK6FR15   R      021JUNELK6PC0E S  031TF                                 V 0102001025        -300          A JS   413312110
SUSAP KBTVK6FR15   R      030RW15 K6PG0GY M 031TF                                   00357             -300          A JS   413322110
SUSAP KBTVK6FR15   R      040         0  M     CA                     1458        + 01000                           A JS   413331505
SUSAP KBTVK6FR15   R      050YUNUDK6PC0EY  R010DF                                 + 05600                           A JS   413341812
SUSAP KBTVK6FR15   R      060YUNUDK6PC0EE  R   HM                     04200050    + 05600                           A JS   413351505
SUSAP KBTVK6FR33-Y ACREME 010CREMEK6PC0E  A    IF                                             18000                 B PS   413362601
SUSAP KBTVK6FR33-Y ACREME 020SILDEK6PC0EE B 010TF                                 + 06000                           B PS   413372601
SUSAP KBTVK6FR33-Y ASILDE 010SILDEK6PC0EE AR   HF                     31090050    + 06000     18000                 B PS   413382601
SUSAP KBTVK6FR33-Y R      010SILDEK6PC0E  I    IF                                 + 06000     18000                 B PS   413391812
SUSAP KBTVK6FR33-Y R      011FOGMOK6PC0E    010TF                                 + 04600                           B PS   413402110
SUSAP KBTVK6FR33-Y R      020ZABOXK6PC1E  F 010TF                                 + 03900                           B PS   413412110
SUSAP KBTVK6FR33-Y R      020ZABOXK6PC2WN          N          ALNAV                                                   PS   413421812
SUSAP KBTVK6FR33-Y R      021WENKIK6PC0E S  031TF                                 V 0212002120        -347          B PS   413432601
SUSAP KBTVK6FR33-Y R      022IRAGEK6PC0E S  031TF                                 V 0132001320        -347          B PS   413442601
SUSAP KBTVK6FR33-Y R      023NASEEK6PC0E S  031TF                                 V 0094000940        -347          B PS   413452601
SUSAP KBTVK6FR33-Y R      030HABOKK6PC0EY M 031TF                                   00572             -347          B PS   413462601
SUSAP KBTVK6FR33-Y R      040         0  M     CA                     3111        + 00735                           B PS   413472601
SUSAP KBTVK6FR33-Y R      050MANIFK6PC0E    010DF                                                                   B PS   413481812
SUSAP KBTVK6FR33-Y R      060STAEVK6PC0EY   010TF                                 + 03000                           B PS   413492110
SUSAP KBTVK6FR33-Y R      070STAEVK6PC0EE  R   HM                     14570040    + 03000                           B PS   413501812
SUSAP KBTVK6FR33-Z ADONGY 010DONGYK6PC0E  A    IF                                             18000                 A JS   413512601
SUSAP KBTVK6FR33-Z ADONGY 020JANUDK6PC0EE B 010TF                                 + 07000                           A JS   413522601
SUSAP KBTVK6FR33-Z AJANUD 010JANUDK6PC0EE AR   HF                     32610070    + 07000                           A JS   413532601
SUSAP KBTVK6FR33-Z AVAMPY 010VAMPYK6PC0E  A    IF                                             18000                 A JS   413542411
SUSAP KBTVK6FR33-Z AVAMPY 020JANUDK6PC0EE B 010TF                                 + 07000                           A JS   413552601
SUSAP KBTVK6FR33-Z R      010JANUDK6PC0E  I    IF                                 + 07000     18000                 A JS   413562601
SUSAP KBTVK6FR33-Z R      011NIDUQK6PC0E    010TF                                 + 05400                           A JS   413572601
SUSAP KBTVK6FR33-Z R      012HONIBK6PC0E    010TF                                 + 04700                           A JS   413582601
SUSAP KBTVK6FR33-Z R      020EHIKOK6PC1E  F 010TF                                 + 03800                           A JS   413592110
SUSAP KBTVK6FR33-Z R      020EHIKOK6PC2WALPV       ALNAV/VNAV ALNAV                                                   JS   413601310
SUSAP KBTVK6FR33-Z R      021JIDSOK6PC0E S  031TF                                 V 0290002900        -320          A JS   413612601
SUSAP KBTVK6FR33-Z R      030RW33 K6PG0GY M 031TF                                   00389             -320          A JS   413622411
SUSAP KBTVK6FR33-Z R      040         0  M     CA                     3260        + 00584                           A JS   413632211
SUSAP KBTVK6FR33-Z R      050STAEVK6PC0EY   010DF                                 + 03000                           A JS   413641310
SUSAP KBTVK6FR33-Z R      060STAEVK6PC0EE  R   HM                     14570040    + 03000                           A JS   413652110
SUSAP KBTVK6FS01   AJRVIS 010JRVISK6PC0E       IF                                             18000                 0 DS   413662202
SUSAP KBTVK6FS01   AJRVIS 020BTV  K6D 0V       TF                                 + 03400                           0 DS   413672202
SUSAP KBTVK6FS01   AJRVIS 030BTV  K6D 0VE AL   PI BTV K6      0000000026100100D   + 03000                           0 DS   413682601
SUSAP KBTVK6FS01   S      020BTV  K6D 0V  F    IF BTV K6      00000000        D   + 02200     18000       BTV   K6D 0 DS   413692601
SUSAP KBTVK6FS01   S      030OTYIJK6PC0EY M    CF BTV K6      0360003903600039D     00515              000          0 DS   413702601
SUSAP KBTVK6FS01   S      040JRVISK6PC0EYM     CF BTV K6      0360016203600123D   + 03500                           0 DS   413712411
SUSAP KBTVK6FS01   S      050JRVISK6PC0EE  R   HM                     2160T010    + 03500          200              0-DS   413722411
SUSAP KBTVK6GRW01    0041120060 N44275197W073090407         +0072600333022530075R                                          413732205
SUSAP KBTVK6GRW15    0083191460 N44285043W073095716         +0064200306000051150IIBTV1                                     413741808
SUSAP KBTVK6GRW19    0041121860 N44282504W073091104         +0071500329050042075V                                          413751808
SUSAP KBTVK6GRW33    0083193260 N44275996W073083556         +0073000334050055150IIVOE1                                     413761808
SUSAP KBTVK6IIBTV1   011030RW15 N44275277W0730823951458N44284234W0730951470613 08480450300W01505100308                     413772104
SUSAP KBTVK6IIVOE1   011030RW33 N44285626W0731006603258N44280882W0730843040904 09980598320W01505500334                     413782212
SUSAP KBTVK6PR01   RW01 001 0000W01A0N4427519665W07309040730+007260400N4429200680W07309226345106751568000300F40050092BEE55B413792205
SUSAP KBTVK6PR01   RW01 002E      +01015+01015LPV       58044                                                              413802205
SUSAP KBTVK6PR15   RW15 001 0000W15A0N4428504280W07309571635+006420300N4427521835W07308229995106750216000511F400500644ADC20413812110
SUSAP KBTVK6PR15   RW15 002E      +00931+00931LPV       72736                                                              413821505
SUSAP KBTVK6PR33-Z RW33 001Z0000W33A0N4427599565W07308355625+007300320N4428581985W07310097330106750368000552F40050078F37CF3413832411
SUSAP KBTVK6PR33-Z RW33 002E      +01019+01019LPV       65812                                                              413841105
SUSAP KBTVK6SBTV  K6D                 0   0901800492518009005925                                                       M   413852601
`;
