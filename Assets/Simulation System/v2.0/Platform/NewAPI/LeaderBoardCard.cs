using LightSide;
using SimulationSystem.V02.Utility;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class LeaderBoardCard : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI rank;
    [SerializeField] private UniText rankUni;
    [SerializeField] private TextMeshProUGUI userName;
    [SerializeField] private UniText userNameUni;
    [SerializeField] private TextMeshProUGUI score;
    [SerializeField] private UniText scoreUni;
    [SerializeField] private Image background;
    [SerializeField] private Color userCardColor;

    private void Awake()
    {
        TextCompat.ResolveUniText(ref rankUni, rank);
        TextCompat.ResolveUniText(ref userNameUni, userName);
        TextCompat.ResolveUniText(ref scoreUni, score);
    }

    public void SetRankDetails(string userName, int rank, int score,bool isPlayer=false)
    {
        TextCompat.SetText(rankUni, this.rank, rank.ToString());
        TextCompat.SetText(userNameUni, this.userName, userName);
        TextCompat.SetText(scoreUni, this.score, score.ToString());

        if (isPlayer)
        {
            this.background.color=userCardColor;
        }
    }
}
